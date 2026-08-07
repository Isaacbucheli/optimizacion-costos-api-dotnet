using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>
/// Trae las recomendaciones de la matriz WAF de un cliente, al grano recomendación × canónica ×
/// tracking (mismo universo que <c>BuildExportRowsAsync</c> de <c>WafController</c>, del que sale
/// este SQL). Recibe una <see cref="SqlConnection"/> ya abierta: no asegura el schema WAF ni
/// administra la conexión, eso es responsabilidad del ensamblador que lo llama junto a los demás
/// recolectores del informe.
/// </summary>
public static class MatrizRecolector
{
    /// <summary>
    /// A diferencia del export de Excel (que filtra por seguridad gestionada externamente y por
    /// suscripción seleccionada), este SQL no lleva esos dos filtros: son parámetros de la pantalla
    /// de la matriz, no del universo de datos que el informe necesita reportar. Por la misma razón
    /// el conteo de recursos lee la columna denormalizada <c>r.resource_count</c> directamente (la
    /// "ruta rápida" de <see cref="Waf.WafSubscriptionFilter.ResourceCountExpr"/> sin selección).
    /// </summary>
    internal const string Sql = """
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
            r.resource_count,
            c.is_excluded
        FROM dbo.waf_recommendation r
        INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = r.canonical_id
        LEFT JOIN dbo.waf_recommendation_tracking t
            ON t.client_id = r.client_id AND t.canonical_id = r.canonical_id
        WHERE r.client_id = @clientId
          AND r.is_active = 1
          AND COALESCE(r.is_dismissed, 0) = 0
        ORDER BY c.pillar_number, r.impact_number, c.review_scope_es
        """;

    public static async Task<IReadOnlyList<MatrizFila>> LeerAsync(
        SqlConnection conn, int clientId, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));

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
