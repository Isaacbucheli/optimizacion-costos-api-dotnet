using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Calculadora de Cosmos DB. Port 1:1 de app/calculators/cosmos_account.py
/// (clase CosmosAccountCalculator, service_key "cosmos").
///
/// Reglas:
/// - Serverless: consumo variable. No se estima con $0 fijo (variable_pricing, payg NULL).
/// - Provisioned o Autoscale: manual_required. El usuario debe meter el costo real
///   desde Cost Management vía endpoint PUT /cost-results/{id}/manual-cost.
/// - No aplica RI (Reserved Capacity requiere análisis aparte con mínimo 10K RU/s).
/// - NUNCA inventa costo.
/// </summary>
public sealed class CosmosAccountCalculator : ICostCalculator
{
    // Cosmos no consulta el repositorio de precios ni las constantes; se inyectan por
    // uniformidad con las demás calculadoras del motor (mismo patrón de DI).
    private readonly IPriceRepository _prices;
    private readonly IPricingConstants _constants;

    public CosmosAccountCalculator(IPriceRepository prices, IPricingConstants constants)
    {
        _prices = prices;
        _constants = constants;
    }

    public IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId)
    {
        var results = new List<CostResult>();

        foreach (var r in resources)
        {
            var result = new CostResult(r.ResourceId, analysisId, "cosmos");

            var isServerless = r.GetBool("is_serverless");
            var apiKind = r.GetString("api_kind") ?? "";
            var regionCount = r.GetInt("region_count") ?? 1;  // int(r.get("region_count") or 1)
            var multiRegionWrites = r.GetBool("multi_region_writes");

            result.RiApplies = false;
            result.RiNotApplicableReason =
                "Cosmos no soporta RI estándar; Reserved Capacity requiere análisis aparte";

            if (isServerless)
            {
                result.CalculationStatus = "variable_pricing";
                result.IsVariablePricing = true;
                result.CalculationNotes =
                    "Serverless: cobro 100% por consumo. Requiere datos de uso " +
                    "o Cost Management para estimacion real.";
            }
            else
            {
                result.CalculationStatus = "manual_required";
                result.CalculationNotes =
                    $"API kind={apiKind}. Provisioned/autoscale requiere throughput real " +
                    $"por base/contenedor o Cost Management. regiones={regionCount}, " +
                    $"multi_write={multiRegionWrites}.";
            }

            results.Add(result);
        }

        return results;
    }
}
