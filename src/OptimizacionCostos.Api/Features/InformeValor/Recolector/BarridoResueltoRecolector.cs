using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>Un hallazgo del barrido de optimización en estado resuelto, con el snapshot del
/// hallazgo tal como se vio en su último scan. <see cref="ResolvedByKind"/>: 'manual' = lo
/// marcó una persona (autoría declarada), 'auto' = dejó de aparecer en el barrido (el recurso
/// pudo borrarlo el cliente), null = histórico anterior a la columna (indeterminado). El mapeo
/// a la etiqueta de autoría del informe es de la calculadora (entrega 6).</summary>
public sealed record BarridoResueltoFila(
    string CheckId,
    string SubscriptionId,
    string AzureResourceId,
    string? ResourceName,
    string? ResourceType,
    decimal? EstimatedMonthlySavings,
    string Currency,
    DateTime ResueltoEn,
    string? ResueltoPor,
    string? ResolvedByKind,
    string? Notas);

/// <summary>El registro del barrido con su degradación declarada. Molde para cualquier fuente
/// futura del registro de lo ejecutado (punto de extensión del plan).</summary>
public sealed record RegistroBarrido(bool Medido, string? Motivo, IReadOnlyList<BarridoResueltoFila> Filas)
{
    public static RegistroBarrido NoAutorizado(string motivo) => new(false, motivo, []);
    public static RegistroBarrido SinBarrido() => new(false,
        "El cliente no tiene ningún barrido de optimización corrido.", []);
}

/// <summary>Lee los hallazgos resueltos del barrido de optimización de tenant (join de
/// <c>dbo.optimization_finding_state</c> con <c>dbo.optimization_finding</c> por
/// <c>fingerprint</c> + <c>scan_id = last_seen_scan_id</c>: el snapshot del hallazgo tal como se
/// vio la última vez, no hace falta filtrar también por <c>client_id</c> en el join porque
/// <c>fingerprint</c> ya es SHA256 de <c>"{clientId}|{checkId}|{azureResourceId}"</c>
/// (<see cref="OptimizacionCostos.Api.Features.Optimization.Finding.Fingerprint(int)"/>) — no hay
/// colisión entre clientes.
///
/// <para>NO decide la autorización: el llamador (entrega 6, el controller, donde vive el
/// contexto de usuario) pasa la doble puerta del spec — permiso del módulo Optimization del
/// llamador Y <c>OptimizationService.AccessAllowed(email)</c> (lista
/// <c>OPTIMIZATION_ALLOWED_EMAILS</c> de <c>AppConfig</c>; lista vacía = abierto) — y usa
/// <see cref="RegistroBarrido.NoAutorizado"/> cuando no pasa. El llamador también debe correr
/// <c>OptimizationService.EnsureSchemaAsync</c> antes de invocar <see cref="LeerAsync"/>: las
/// tablas del barrido no las asegura <c>SqlInsumosBdRecolector</c> (no forma parte de
/// <c>IInsumosBdRecolector</c>/<c>InsumosBd</c> a propósito, ver el plan).</para></summary>
public static class BarridoResueltoRecolector
{
    /// <summary>Si el cliente nunca corrió el barrido, "cero hallazgos resueltos" mentiría (D9):
    /// el eje queda sin medir, no en cero. <see cref="LeerAsync"/> consulta esto primero para
    /// distinguir los dos casos.</summary>
    internal const string SqlScanCount =
        "SELECT COUNT(*) FROM dbo.optimization_scan WHERE client_id = @clientId";

    internal const string Sql = """
        SELECT f.check_id, f.subscription_id, f.azure_resource_id, f.resource_name, f.resource_type,
               f.estimated_monthly_savings, f.currency, s.updated_at, s.updated_by,
               s.resolved_by_kind, s.notes
        FROM dbo.optimization_finding_state s
        INNER JOIN dbo.optimization_finding f
            ON f.fingerprint = s.fingerprint AND f.scan_id = s.last_seen_scan_id
        WHERE s.client_id = @clientId AND s.state = 'resuelto'
        ORDER BY s.updated_at
        """;

    public static async Task<RegistroBarrido> LeerAsync(SqlConnection conn, int clientId, CancellationToken ct = default)
    {
        await using (var count = conn.CreateCommand())
        {
            count.CommandText = SqlScanCount;
            count.Parameters.Add(new SqlParameter("@clientId", clientId));
            var scans = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
            if (scans == 0) return RegistroBarrido.SinBarrido();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));
        var filas = new List<BarridoResueltoFila>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            filas.Add(MapearFila(rd));
        return new RegistroBarrido(true, null, filas);
    }

    internal static BarridoResueltoFila MapearFila(SqlDataReader r) => new(
        CheckId: r.GetString(0),
        SubscriptionId: r.GetString(1),
        AzureResourceId: r.GetString(2),
        ResourceName: r.IsDBNull(3) ? null : r.GetString(3),
        ResourceType: r.IsDBNull(4) ? null : r.GetString(4),
        EstimatedMonthlySavings: r.IsDBNull(5) ? null : r.GetDecimal(5),
        Currency: r.GetString(6),
        ResueltoEn: r.GetDateTime(7),
        ResueltoPor: r.IsDBNull(8) ? null : r.GetString(8),
        ResolvedByKind: r.IsDBNull(9) ? null : r.GetString(9),
        Notas: r.IsDBNull(10) ? null : r.GetString(10));
}
