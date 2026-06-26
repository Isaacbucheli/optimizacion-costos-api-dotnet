using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Calculadora de Azure Cache for Redis. Port 1:1 de
/// app/calculators/redis_cache.py (clase RedisCacheCalculator, service_key "redis").
///
/// Reglas:
/// - Azure Managed Redis (Enterprise): aplica RI.
/// - Azure Cache for Redis Classic (Basic, Standard, Premium): aplica RI excepto Basic.
/// - precio mensual = precio_hourly * 730.
/// - sku_capacity es el numero de SKU.
/// </summary>
public sealed class RedisCacheCalculator : ICostCalculator
{
    private readonly IPriceRepository _prices;
    private readonly IPricingConstants _constants;

    public RedisCacheCalculator(IPriceRepository prices, IPricingConstants constants)
    {
        _prices = prices;
        _constants = constants;
    }

    /// <summary>Normaliza la region: minusculas y sin espacios (igual que _normalize_region en Python).</summary>
    private static string NormalizeRegion(string? location)
    {
        if (string.IsNullOrEmpty(location)) return "";
        return location.ToLowerInvariant().Replace(" ", "");
    }

    public IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId)
    {
        var results = new List<CostResult>();
        var hours = _constants.HoursPerMonth();

        foreach (var r in resources)
        {
            var result = new CostResult(r.ResourceId, analysisId, "redis");

            // sku_name = r.get("sku_name") or ""
            var skuName = r.GetString("sku_name") ?? "";
            // sku_family = r.get("sku_family") or ""
            var skuFamily = r.GetString("sku_family") ?? "";
            // sku_capacity = r.get("sku_capacity") or 0
            var skuCapacity = r.GetInt("sku_capacity") ?? 0;
            // location = r.get("location")
            var location = r.GetString("location");

            var region = NormalizeRegion(location);
            if (string.IsNullOrEmpty(skuName) || string.IsNullOrEmpty(region))
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = "Missing sku_name or location";
                results.Add(result);
                continue;
            }

            RedisPrices priceData;
            try
            {
                priceData = _prices.GetRedisPrices(skuName, skuCapacity, region);
            }
            catch (Exception ex)
            {
                // No loguear secretos; solo el nombre del tipo de excepcion.
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = $"Price lookup error: {ex.GetType().Name}";
                results.Add(result);
                continue;
            }

            var paygHourly = priceData.PaygHourly;
            if (paygHourly is null)
            {
                result.CalculationStatus = "manual_required";
                result.CalculationNotes =
                    $"No hubo candidato real en Azure Retail Prices API para Redis " +
                    $"sku={skuName} family={skuFamily} capacity={skuCapacity} region={region}. " +
                    $"Requiere validacion manual.";
                results.Add(result);
                continue;
            }

            result.PaygHourly = paygHourly.Value;
            result.PaygMonthly = paygHourly.Value * hours;

            // RI: NO aplica en Classic Basic
            var skuLower = skuName.ToLowerInvariant();
            var isClassicBasic = skuLower.Contains("basic") && !skuLower.Contains("enterprise");
            if (isClassicBasic)
            {
                result.RiApplies = false;
                result.RiNotApplicableReason = "Classic Basic no soporta RI";
            }
            else
            {
                result.RiApplies = true;
                var ri1yTotal = priceData.Ri1yTotal;
                var ri3yTotal = priceData.Ri3yTotal;
                if (ri1yTotal is not null)
                {
                    result.Ri1yMonthly = ri1yTotal.Value / 12.0;
                }
                if (ri3yTotal is not null)
                {
                    result.Ri3yMonthly = ri3yTotal.Value / 36.0;
                }
                result.ComputeSavings();
                result.DiscardNonSavingRi();
            }

            result.CalculationNotes =
                $"sku={skuName} family={skuFamily} capacity={skuCapacity} region={region} " +
                $"match={priceData.MatchStrategy ?? "deterministic"}";
            results.Add(result);
        }

        return results;
    }
}
