namespace OptimizacionCostos.Api.Features.FinOpsData;

/// <summary>
/// Etiquetas de elegibilidad RI por fila de cost_results.
/// Semántica del dataset (VALIDADA empíricamente 2026-07-02 contra Retail API):
///   x_CommitmentDiscountSpendEligibility  = tiene precio de RESERVA (RI)
///   x_CommitmentDiscountUsageEligibility  = tiene precio de SAVINGS PLAN
/// (El naming es contrario a la convención FOCUS spend=SP/usage=RI; la doc de MS Learn
/// describe las columnas correctamente. Evidencia: Container Apps vCPU 00046018-... =
/// Not Eligible/Eligible (sin RI, con SP); Redis E100 00020329-... = Eligible/Not Eligible.)
/// El CSV es whitelist: meter ausente = unknown, NUNCA not_eligible.
/// </summary>
public static class RiEligibility
{
    public const string Eligible = "eligible";
    public const string NotEligible = "not_eligible";
    public const string Unknown = "unknown";

    public static string Label(string? meterId, IReadOnlyDictionary<string, bool> riMap)
        => string.IsNullOrEmpty(meterId) || !riMap.TryGetValue(meterId, out var ri)
            ? Unknown
            : ri ? Eligible : NotEligible;
}

/// <summary>Mapa estático service_key del catálogo → categoría FOCUS (contrastado con Services.csv).</summary>
public static class FinOpsServiceCategoryMap
{
    public static readonly IReadOnlyDictionary<string, string> ByServiceKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vms"] = "Compute",
            ["sql_vm"] = "Compute",
            ["appservice"] = "Compute",
            ["disks"] = "Storage",
            ["sql"] = "Databases",
            ["sql_mi"] = "Databases",
            ["mysql"] = "Databases",
            ["elastic_pool"] = "Databases",
            ["cosmos"] = "Databases",
            ["redis"] = "Databases",
            ["synapse_dw"] = "Analytics",
            ["public_ip"] = "Networking",
        };
}
