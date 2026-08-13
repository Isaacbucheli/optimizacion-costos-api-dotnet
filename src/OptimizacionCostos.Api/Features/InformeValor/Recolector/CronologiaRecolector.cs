using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>Una entrada de la bitácora del tracking de la matriz, con el nombre de su
/// recomendación. Es la fuente de la cronología del informe (decisión 2026-08-13: derivada,
/// sin tabla nueva). Solo registra hitos de BIT: cuando el relato necesite la respuesta del
/// cliente, la línea de tiempo lo declara — eso es redacción de la entrega 7.</summary>
public sealed record HitoFila(
    DateTime Fecha,
    string Campo,
    string? ValorAnterior,
    string? ValorNuevo,
    string? Autor,
    string? MatrixCode,
    string Recomendacion,
    int PillarNumber);

/// <summary>
/// Trae la cronología de un cliente directamente de <c>dbo.waf_tracking_history</c> (la bitácora
/// que ya alimenta la pestaña de tracking de la matriz WAF): cada cambio de campo que un consultor
/// registra ahí (avance, fechas de remediación, esfuerzo proyectado, bitácora de ejecución,
/// prioridad, notas internas) es un hito potencial de la línea de tiempo del informe. Recibe una
/// <see cref="SqlConnection"/> ya abierta, mismo contrato que el resto de recolectores del módulo
/// (<see cref="HallazgoResueltoRecolector"/>, <see cref="MatrizRecolector"/>): no asegura el schema
/// WAF ni administra la conexión.
///
/// <para>El join a <c>dbo.waf_recommendation</c> es por <c>(client_id, canonical_id)</c>,
/// la misma pareja que <c>UQ_waf_rec_client_canonical</c> declara única en el schema
/// (<see cref="WafSchema"/>): no hay riesgo de duplicar hitos por múltiples recomendaciones del
/// mismo canónico para un cliente.</para>
///
/// <para>NO filtra por <c>field_changed</c> a propósito (punto de extensión declarado del plan):
/// decidir qué campos se traducen a un hito legible y con qué redacción es trabajo de la entrega 7,
/// no de este recolector. Mismo filtro de seguridad gestionada externamente que
/// <see cref="HallazgoResueltoRecolector"/>/<see cref="MatrizRecolector"/>: un hito de una
/// recomendación del pilar de Seguridad delataría el hallazgo que el cliente pidió no ver.</para>
/// </summary>
public static class CronologiaRecolector
{
    internal static string Sql(bool seguridadGestionadaExternamente)
    {
        var secFilter = seguridadGestionadaExternamente
            ? $" AND c.pillar_number <> {WafConstants.SecurityPillar}" : "";
        return $"""
            SELECT h.changed_at, h.field_changed, h.old_value, h.new_value, h.changed_by,
                   r.matrix_code, c.review_scope_es, c.pillar_number
            FROM dbo.waf_tracking_history h
            INNER JOIN dbo.waf_recommendation r
                ON r.client_id = h.client_id AND r.canonical_id = h.canonical_id
            INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = h.canonical_id
            WHERE h.client_id = @clientId{secFilter}
            ORDER BY h.changed_at
            """;
    }

    public static async Task<IReadOnlyList<HitoFila>> LeerAsync(
        SqlConnection conn, int clientId, bool seguridadGestionadaExternamente, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql(seguridadGestionadaExternamente);
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));
        var items = new List<HitoFila>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            items.Add(MapearFila(rd));
        return items;
    }

    internal static HitoFila MapearFila(SqlDataReader r) => new(
        Fecha: r.GetDateTime(0),
        Campo: r.GetString(1),
        ValorAnterior: r.IsDBNull(2) ? null : r.GetString(2),
        ValorNuevo: r.IsDBNull(3) ? null : r.GetString(3),
        Autor: r.IsDBNull(4) ? null : r.GetString(4),
        MatrixCode: r.IsDBNull(5) ? null : r.GetString(5),
        Recomendacion: r.GetString(6),
        PillarNumber: (int)r.GetByte(7));
}
