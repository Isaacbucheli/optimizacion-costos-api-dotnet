using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>
/// Un hallazgo de <c>waf_resource_finding</c> con <c>status='resolved'</c>, al grano recurso: la
/// pieza de la Tarea 3 de la entrega 2d (E3) que le da al bloque de atribución la única evidencia
/// de autoría que existe en la plataforma. <see cref="SubscriptionId"/>/<see cref="ResourceGroup"/>/
/// <see cref="ResourceName"/> son la terna con la que <c>AtribucionCalculador</c> cruza esto contra
/// facturación (E3, E6): las tres son <c>NOT NULL</c> en <c>waf_resource_finding</c>, a diferencia
/// de <see cref="RetiroFila"/>/<see cref="AdvisorFila"/>, que sí toleran campos nulos.
///
/// <para><see cref="ResolvedAt"/> es nullable porque la columna lo es en el schema
/// (<c>resolved_at DATETIME2 NULL</c>): en la práctica siempre se escribe junto con la transición a
/// <c>resolved</c>, pero un hallazgo resuelto sin fecha de resolución no se puede ubicar dentro de
/// ningún período, así que <c>AtribucionCalculador</c> lo descarta (D0: el filtro de rango vive en
/// la calculadora, no acá, igual que <see cref="ConsumoCalculador"/>/<see cref="AdvisorRecolector"/>
/// no filtran por fecha en el recolector).</para>
///
/// <para><see cref="MatrixCode"/>/<see cref="Hallazgo"/>/<see cref="PillarNumber"/> viajan solo para
/// que el modelo pueda anotar QUÉ recomendación resolvió cada recurso (el consultor tiene que poder
/// defender la cifra con la planilla abierta): no participan del cruce por terna.</para>
/// </summary>
public sealed record HallazgoResueltoFila(
    string SubscriptionId,
    string SubscriptionName,
    string ResourceGroup,
    string ResourceName,
    DateOnly? ResolvedAt,
    string? MatrixCode,
    string Hallazgo,
    int PillarNumber);

/// <summary>
/// Trae los hallazgos RESUELTOS de la matriz WAF de un cliente, al grano recurso (una fila por
/// recurso afectado, igual que <see cref="AdvisorRecolector"/>, pero <c>f.status = 'resolved'</c> en
/// vez de <c>'active'</c>: universo disjunto, misma tabla). Recibe una <see cref="SqlConnection"/>
/// ya abierta: no asegura el schema WAF ni administra la conexión, eso es responsabilidad de quien
/// llama, junto a los demás recolectores del informe.
///
/// <para><b>Mismos dos filtros que <see cref="AdvisorRecolector"/>/<see cref="MatrizRecolector"/>,
/// por el mismo motivo (ver el comentario de clase de <see cref="MatrizRecolector"/>):
/// <c>security_managed_externally</c> oculta el pilar de Seguridad entero, y solo cuentan hallazgos
/// de suscripciones administradas.</b> Un hallazgo resuelto en una suscripción que el cliente dejó
/// de administrar no es información vigente: ni la pantalla WAF ni el resto del informe lo verían
/// tampoco. <see cref="WafConstants.ManualSubscriptionId"/> ('importado') queda excluido sin
/// excepción (a diferencia de <see cref="MatrizRecolector"/>, que sí la exceptúa para no subcontar
/// <c>resource_count</c> de una recomendación mixta): acá no hay conteo agregado que proteger, y un
/// hallazgo importado nunca puede tener una terna real de facturación con la que cruzar, así que
/// incluirlo no cambiaría ningún resultado — se excluye solo por higiene, igual que
/// <see cref="AdvisorRecolector"/>.</para>
///
/// <para>No filtra por fecha: <see cref="HallazgoResueltoFila.ResolvedAt"/> viaja crudo y es
/// <c>AtribucionCalculador</c> quien decide qué cae dentro del período del informe (D0, mismo
/// patrón que el resto del módulo — ver <see cref="ConsumoCalculador.EnRango"/>).</para>
/// </summary>
public static class HallazgoResueltoRecolector
{
    /// <summary>
    /// SQL dinámico, mismo motivo que <see cref="MatrizRecolector.Sql"/>: los dos filtros cambian
    /// el WHERE según el cliente.
    /// </summary>
    internal static string Sql(IReadOnlyList<string> suscripcionesAdministradas, bool seguridadGestionadaExternamente)
    {
        var secFilter = seguridadGestionadaExternamente ? $" AND c.pillar_number <> {WafConstants.SecurityPillar}" : "";
        var subFilter = WafSubscriptionFilter.FindingPredicate("f", suscripcionesAdministradas);
        return $"""
            SELECT
                f.subscription_id,
                f.subscription_name,
                f.resource_group,
                f.resource_name,
                f.resolved_at,
                r.matrix_code,
                c.review_scope_es,
                c.pillar_number
            FROM dbo.waf_resource_finding f
            INNER JOIN dbo.waf_recommendation r ON r.recommendation_id = f.recommendation_id
            INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = r.canonical_id
            WHERE r.client_id = @clientId
              AND f.status = 'resolved'
              AND f.subscription_id <> '{WafConstants.ManualSubscriptionId}'{secFilter}{subFilter}
            ORDER BY f.subscription_name, f.resource_group, f.resource_name
            """;
    }

    /// <param name="suscripcionesAdministradas">Ver <see cref="MatrizRecolector.LeerAsync"/>: misma
    /// lista, mismo motivo para devolver vacío sin consultar cuando no hay ninguna.</param>
    /// <param name="seguridadGestionadaExternamente">Ver el comentario de clase.</param>
    public static async Task<IReadOnlyList<HallazgoResueltoFila>> LeerAsync(
        SqlConnection conn, int clientId, IReadOnlyList<string> suscripcionesAdministradas,
        bool seguridadGestionadaExternamente, CancellationToken ct = default)
    {
        if (suscripcionesAdministradas.Count == 0) return [];

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql(suscripcionesAdministradas, seguridadGestionadaExternamente);
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));
        WafSubscriptionFilter.AddParameters(cmd, suscripcionesAdministradas);

        var items = new List<HallazgoResueltoFila>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            items.Add(MapearFila(rd));
        return items;
    }

    internal static HallazgoResueltoFila MapearFila(SqlDataReader r) => new(
        SubscriptionId: r.GetString(0),
        SubscriptionName: r.GetString(1),
        ResourceGroup: r.GetString(2),
        ResourceName: r.GetString(3),
        ResolvedAt: r.IsDBNull(4) ? null : DateOnly.FromDateTime(r.GetDateTime(4)),
        MatrixCode: r.IsDBNull(5) ? null : r.GetString(5),
        Hallazgo: r.GetString(6),
        PillarNumber: (int)r.GetByte(7));
}
