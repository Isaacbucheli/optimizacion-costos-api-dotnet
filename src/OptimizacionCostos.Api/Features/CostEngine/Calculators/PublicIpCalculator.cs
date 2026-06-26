using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Calculadora de IPs Públicas. Port 1:1 de app/calculators/public_ip.py::PublicIPCalculator.
///
/// Reglas:
/// - Solo se muestran IPs huérfanas (sin recurso asociado): is_orphan truthy.
/// - Precio mensual desde Azure Retail Prices API (vía IPriceRepository, que normaliza la región internamente).
/// - No aplica RI.
/// </summary>
public sealed class PublicIpCalculator : ICostCalculator
{
    private const string ServiceKey = "public_ip";

    private readonly IPriceRepository _prices;
    private readonly IPricingConstants _constants;

    public PublicIpCalculator(IPriceRepository prices, IPricingConstants constants)
    {
        _prices = prices ?? throw new ArgumentNullException(nameof(prices));
        // Esta calculadora no usa constantes hoy, pero las recibe por inyección
        // para mantener la firma uniforme con el resto de calculadoras.
        _constants = constants ?? throw new ArgumentNullException(nameof(constants));
    }

    public IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId)
    {
        var results = new List<CostResult>();

        foreach (var r in resources)
        {
            // Solo IPs huérfanas (bool(r.get("is_orphan"))).
            var isOrphan = r.GetBool("is_orphan");
            if (!isOrphan)
            {
                continue;
            }

            var result = new CostResult(r.ResourceId, analysisId, ServiceKey);

            // sku_name = r.get("sku_name") or "" ; allocation_method = r.get("allocation_method") or "Static".
            var skuName = StringOr(r.GetString("sku_name"), "");
            var allocationMethod = StringOr(r.GetString("allocation_method"), "Static");
            var location = r.GetString("location"); // puede ser null (== None en Python).

            double? monthly;
            try
            {
                // GetPublicIpMonthlyPrice normaliza la región internamente (no normalizar aquí).
                monthly = _prices.GetPublicIpMonthlyPrice(skuName, location ?? "", allocationMethod);
            }
            catch
            {
                // Cualquier fallo en el lookup => sin precio (manual). No exponer detalles.
                monthly = null;
            }

            if (monthly is null)
            {
                result.CalculationStatus = "manual_required";
                result.CalculationNotes =
                    $"No hubo candidato real en Azure Retail Prices API para Public IP " +
                    $"sku={skuName} allocation={allocationMethod} region={location}.";
                results.Add(result);
                continue;
            }

            result.PaygMonthly = monthly;
            result.RiApplies = false;
            result.RiNotApplicableReason = "IPs no soportan RI";

            result.CalculationNotes =
                $"sku={skuName} allocation={allocationMethod} category=orphan";

            results.Add(result);
        }

        return results;
    }

    /// <summary>Imita <c>valor or fallback</c> de Python: usa el fallback si la cadena es null o vacía.</summary>
    private static string StringOr(string? value, string fallback)
        => string.IsNullOrEmpty(value) ? fallback : value;
}
