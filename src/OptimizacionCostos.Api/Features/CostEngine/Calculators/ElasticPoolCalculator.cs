using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Calculadora de Azure SQL Elastic Pool. Port 1:1 de
/// app/calculators/elastic_pool.py::ElasticPoolCalculator (service_key "elastic_pool").
///
/// Dos modelos de facturación (determinados por sku_tier desde Azure):
/// - DTU (Basic/Standard/Premium): meter '{N} DTU Pack' por día.
/// - vCore (GeneralPurpose/BusinessCritical/Hyperscale): meter '{N} vCore' por hora.
///
/// Blindaje (NO inventar cifras):
/// - Solo se reporta monto si hay match EXACTO de capacidad y unidad esperada.
/// - Sin match → 'price_not_found' (queda en blanco con nota), nunca aproxima.
/// - Las BD que viven dentro del pool se costean en $0 'incluido en pool'
///   (lo hace el calculador 'sql', no este), para evitar doble conteo.
///
/// PAYG únicamente. RI diferido (ver nota) — coherente con SQL provisioned.
/// </summary>
public sealed class ElasticPoolCalculator(IPriceRepository prices, IPricingConstants constants) : ICostCalculator
{
    private readonly IPriceRepository _prices = prices;
    private readonly IPricingConstants _constants = constants;

    /// <summary>Normaliza la región (lower + sin espacios), igual que _normalize_region en Python.</summary>
    private static string NormalizeRegion(string? location)
    {
        if (string.IsNullOrEmpty(location))
        {
            return "";
        }
        return location.ToLowerInvariant().Replace(" ", "");
    }

    public IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId)
    {
        var results = new List<CostResult>();
        var hours = _constants.HoursPerMonth();

        foreach (var r in resources)
        {
            var result = new CostResult(r.ResourceId, analysisId, "elastic_pool");

            var skuTier = r.GetString("sku_tier") ?? "";
            var skuFamily = r.GetString("sku_family") ?? "";
            // Python: int(r.get("capacity") or r.get("sku_capacity") or 0)
            // El 'or' cae al siguiente si el valor es falsy (None/0); replicamos esa cadena.
            var capacity = FirstTruthyInt("capacity", "sku_capacity", r);
            var region = NormalizeRegion(r.GetString("location"));

            if (string.IsNullOrEmpty(region) || string.IsNullOrEmpty(skuTier) || capacity <= 0)
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = "Missing region/sku_tier/capacity";
                results.Add(result);
                continue;
            }

            ElasticPoolPrices? priceData;
            try
            {
                priceData = _prices.GetElasticPoolPrices(skuTier, skuFamily, capacity, region);
            }
            catch (Exception ex)
            {
                // No exponer detalles; solo el nombre del tipo, igual que el Python.
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = $"Price lookup error: {ex.GetType().Name}";
                results.Add(result);
                continue;
            }

            if (priceData is null || priceData.PaygHourly is null)
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes =
                    $"Sin precio exacto para Elastic Pool tier={skuTier} " +
                    $"family={skuFamily} capacity={capacity} region={region}. " +
                    $"Requiere validación manual (no se aproxima).";
                results.Add(result);
                continue;
            }

            var paygHourly = priceData.PaygHourly.Value;
            result.PaygHourly = paygHourly;
            result.PaygMonthly = paygHourly * hours;

            result.RiApplies = false;
            result.RiNotApplicableReason = "RI de pool diferido; solo PAYG";
            result.CalculationNotes =
                $"Elastic Pool modelo={priceData.Model} tier={skuTier} " +
                $"family={skuFamily} capacity={capacity} region={region} " +
                $"meter={priceData.MeterName} sku={priceData.MatchedSku}; " +
                $"las BD del pool se costean en $0 (incluido).";
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Replica <c>int(r.get("capacity") or r.get("sku_capacity") or 0)</c>: toma el primer
    /// valor "truthy" (no null y != 0) de las claves dadas, o 0 si ninguno aplica.
    /// </summary>
    private static int FirstTruthyInt(string firstKey, string secondKey, ResourceRow r)
    {
        var first = r.GetInt(firstKey);
        if (first is { } f && f != 0)
        {
            return f;
        }
        var second = r.GetInt(secondKey);
        if (second is { } s && s != 0)
        {
            return s;
        }
        return 0;
    }
}
