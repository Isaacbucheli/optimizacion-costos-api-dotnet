using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>
/// Trae los hallazgos activos de Azure Advisor de un cliente, al grano recomendación × recurso
/// (el mismo del export de la matriz). Recibe una <see cref="SqlConnection"/> ya abierta: no
/// asegura el schema WAF ni administra la conexión, eso es responsabilidad del ensamblador que lo
/// llama junto a los demás recolectores del informe.
/// </summary>
public static class AdvisorRecolector
{
    /// <summary>
    /// Sentinela que el importador de la matriz Excel escribe en subscription_id para los
    /// hallazgos cargados a mano (ver <see cref="WafConstants.ManualSubscriptionId"/>). Sin
    /// excluirlos el informe publica "(matriz historica)" como si fuera una suscripción real del
    /// cliente, con su propio porcentaje sobre el total. El literal queda fijo en el SQL (no es
    /// un parámetro) a propósito: el test de esta clase inspecciona el texto de <see cref="Sql"/>
    /// para confirmar que el filtro sigue ahí.
    /// </summary>
    internal const string Sql = """
        SELECT
            c.pillar_number,
            r.impact_number,
            c.advisor_name,
            c.advisor_name_en,
            r.canonical_id,
            r.matrix_code,
            r.source,
            f.subscription_id,
            f.subscription_name,
            f.resource_name,
            f.resource_type,
            TRY_CAST(COALESCE(
                JSON_VALUE(f.additional_info, '$.extendedProperties.annualSavingsAmount'),
                JSON_VALUE(f.additional_info, '$."Potential Annual Cost Savings"')
            ) AS DECIMAL(18,2)) AS ahorro_anual,
            JSON_VALUE(f.additional_info, '$.extendedProperties.savingsCurrency') AS moneda_ahorro
        FROM dbo.waf_resource_finding f
        INNER JOIN dbo.waf_recommendation r ON r.recommendation_id = f.recommendation_id
        INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = r.canonical_id
        WHERE r.client_id = @clientId
          AND r.is_active = 1
          AND COALESCE(r.is_dismissed, 0) = 0
          AND f.status = 'active'
          AND f.subscription_id <> 'importado'
        ORDER BY c.pillar_number, r.impact_number, f.subscription_name, f.resource_name
        """;

    /// <summary>Texto de "sin suscripción" cuando subscription_name viene vacío en la base.</summary>
    private const string SinSuscripcion = "(sin suscripción)";

    public static async Task<IReadOnlyList<AdvisorFila>> LeerAsync(
        SqlConnection conn, int clientId, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));

        var items = new List<AdvisorFila>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            items.Add(MapearFila(rd));
        return items;
    }

    internal static AdvisorFila MapearFila(SqlDataReader r)
    {
        var pillarNumber = (int)r.GetByte(0);
        int? impactNumber = r.IsDBNull(1) ? null : r.GetByte(1);
        var subscriptionName = r.IsDBNull(8) ? null : r.GetString(8);

        return new AdvisorFila(
            PillarNumber: pillarNumber,
            Pilar: EtiquetaPilar(pillarNumber),
            ImpactNumber: impactNumber,
            Impacto: EtiquetaImpacto(impactNumber),
            Recomendacion: r.GetString(2),
            RecomendacionEn: r.IsDBNull(3) ? null : r.GetString(3),
            CanonicalId: r.GetInt32(4),
            MatrixCode: r.IsDBNull(5) ? null : r.GetString(5),
            Source: r.IsDBNull(6) ? null : r.GetString(6),
            SubscriptionId: r.IsDBNull(7) ? null : r.GetString(7),
            SubscriptionName: string.IsNullOrWhiteSpace(subscriptionName) ? SinSuscripcion : subscriptionName,
            ResourceName: r.IsDBNull(9) ? null : r.GetString(9),
            ResourceType: r.GetString(10),
            AhorroAnual: r.IsDBNull(11) ? null : r.GetDecimal(11),
            MonedaAhorro: r.IsDBNull(12) ? null : r.GetString(12));
    }

    /// <summary>
    /// Etiqueta de pilar para el informe: la misma que ve el consultor en la pantalla de la
    /// matriz (<see cref="SqlWafRecommendationStore.PillarSectionNames"/>), no una tabla propia.
    /// Hay tres juegos de etiquetas compitiendo en el repo (ver también
    /// <see cref="WafConstants.PillarLabels"/>); usar uno distinto acá haría que el bloque de
    /// Advisor y el de la matriz se contradigan en la misma página del informe.
    /// </summary>
    internal static string EtiquetaPilar(int pillarNumber) =>
        SqlWafRecommendationStore.PillarSectionNames.TryGetValue(pillarNumber, out var etiqueta) ? etiqueta : "";

    /// <summary>impact_number (1/2/3) a la etiqueta que ve el consultor. Null o fuera de rango → "".</summary>
    internal static string EtiquetaImpacto(int? impactNumber) => impactNumber switch
    {
        1 => "Alto",
        2 => "Medio",
        3 => "Bajo",
        _ => "",
    };
}
