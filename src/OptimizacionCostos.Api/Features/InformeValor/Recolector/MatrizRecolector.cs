using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>
/// Trae las recomendaciones de la matriz WAF de un cliente, al grano recomendación × canónica ×
/// tracking (mismo universo que <c>BuildExportRowsAsync</c> de <c>WafController</c>, del que sale
/// este SQL). Recibe una <see cref="SqlConnection"/> ya abierta: no asegura el schema WAF ni
/// administra la conexión, eso es responsabilidad del ensamblador que lo llama junto a los demás
/// recolectores del informe.
///
/// <para><b>Los dos filtros de la pantalla de la matriz SÍ aplican acá, no son "parámetros de
/// pantalla".</b> <c>security_managed_externally</c> es una bandera por cliente en
/// <c>dbo.clients</c>, y la aplican las tres salidas del producto (pantalla WAF, export a Excel e
/// informe de gestión mensual): cuando el cliente gestiona su seguridad por fuera, las tres ocultan
/// el pilar de Seguridad entero. <see cref="Sql"/> replica exactamente eso
/// (<c>WafController.ListRecommendations</c>/<c>BuildExportRowsAsync</c>,
/// <c>ReportBuilder.WafRecommendationsAsync</c>): mismo <see cref="WafConstants.SecurityPillar"/>,
/// mismo criterio de "pilar entero afuera". El filtro por suscripción administrada tampoco es de
/// pantalla: sin él, una recomendación cuyos hallazgos viven todos en una suscripción que el
/// usuario dejó de administrar queda activa para siempre (<c>SqlWafIngestionStore</c> solo resuelve
/// hallazgos de las suscripciones que la corrida de ingesta escaneó). Se reusa
/// <see cref="WafSubscriptionFilter.ExistsPredicate"/>/<see cref="WafSubscriptionFilter.ResourceCountExpr"/>
/// con la lista de administradas en vez de la selección de la UI: misma mecánica, otro insumo.
/// </para>
/// </summary>
public static class MatrizRecolector
{
    /// <summary>
    /// SQL dinámico: los dos filtros de <see cref="LeerAsync"/> (seguridad gestionada externamente,
    /// suscripciones administradas) cambian el WHERE y la expresión de <c>resource_count</c> según
    /// el cliente, así que no puede ser una constante fija como antes.
    /// </summary>
    internal static string Sql(IReadOnlyList<string> suscripcionesAdministradas, bool seguridadGestionadaExternamente)
    {
        var secFilter = seguridadGestionadaExternamente ? $" AND c.pillar_number <> {WafConstants.SecurityPillar}" : "";
        var subFilter = WafSubscriptionFilter.ExistsPredicate("r", suscripcionesAdministradas);
        var resourceCountExpr = WafSubscriptionFilter.ResourceCountExpr("r", suscripcionesAdministradas);
        return $"""
            SELECT
                r.canonical_id,
                r.matrix_code,
                c.pillar_number,
                c.review_scope_es,
                r.first_seen_at,
                r.impact_number,
                t.priority_override,
                t.projected_bit_effort,
                COALESCE(t.completion_pct, 0) AS completion_pct,
                t.execution_log,
                {resourceCountExpr} AS resource_count,
                c.is_excluded
            FROM dbo.waf_recommendation r
            INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = r.canonical_id
            LEFT JOIN dbo.waf_recommendation_tracking t
                ON t.client_id = r.client_id AND t.canonical_id = r.canonical_id
            WHERE r.client_id = @clientId
              AND r.is_active = 1
              AND COALESCE(r.is_dismissed, 0) = 0{secFilter}{subFilter}
            ORDER BY c.pillar_number, r.impact_number, c.review_scope_es
            """;
    }

    /// <param name="suscripcionesAdministradas">Ids de <c>client_azure_subscriptions</c> activas y
    /// administradas del cliente (ver <c>SqlInsumosBdRecolector.SuscripcionesAdministradasAsync</c>,
    /// predicado único del módulo). Vacía → sin nada que reportar: se devuelve sin consultar, porque
    /// <see cref="WafSubscriptionFilter"/> trata una lista vacía como "sin selección" (todo pasa), el
    /// significado opuesto al que necesita este filtro.</param>
    /// <param name="seguridadGestionadaExternamente">Ver el comentario de clase.</param>
    public static async Task<IReadOnlyList<MatrizFila>> LeerAsync(
        SqlConnection conn, int clientId, IReadOnlyList<string> suscripcionesAdministradas,
        bool seguridadGestionadaExternamente, CancellationToken ct = default)
    {
        if (suscripcionesAdministradas.Count == 0) return [];

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql(suscripcionesAdministradas, seguridadGestionadaExternamente);
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));
        WafSubscriptionFilter.AddParameters(cmd, suscripcionesAdministradas);

        var items = new List<MatrizFila>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            items.Add(MapearFila(rd));
        return items;
    }

    internal static MatrizFila MapearFila(SqlDataReader r)
    {
        var pillarNumber = (int)r.GetByte(2);

        return new MatrizFila(
            CanonicalId: r.GetInt32(0),
            MatrixCode: r.IsDBNull(1) ? null : r.GetString(1),
            PillarNumber: pillarNumber,
            Ambito: AdvisorRecolector.EtiquetaPilar(pillarNumber),
            Hallazgo: r.GetString(3),
            Fecha: r.IsDBNull(4) ? null : DateOnly.FromDateTime(r.GetDateTime(4)),
            ImpactNumber: r.IsDBNull(5) ? null : r.GetByte(5),
            // priority_override crudo (1/2/3), sin la etiqueta "1 - ALTA" que arma el exportador de
            // Excel (ClosedXmlWafExporter.PriorityText): esa traducción es de la calculadora.
            Prioridad: r.IsDBNull(6) ? null : r.GetByte(6).ToString(),
            EsfuerzoTexto: r.IsDBNull(7) ? null : r.GetString(7),
            AvancePct: r.GetInt32(8),
            Registro: r.IsDBNull(9) ? null : r.GetString(9),
            ResourceCount: r.GetInt32(10),
            Excluida: r.GetBoolean(11));
    }
}
