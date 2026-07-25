using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Calculadora de snapshots de managed disks (service_key "snapshots", spec 2026-07-24).
///
/// Azure factura los snapshots por GB/mes del espacio OCUPADO, dato que ni Resource Graph
/// ni ARM exponen; se usa disk_size_gb (tamaño del disco de origen) como techo referencial
/// declarado en calculation_notes. Sin RI (los snapshots no tienen reserva). El precio/GB
/// sale de GetSnapshotPricePerGb (ya existente, meters "{LRS|ZRS|GRS} Snapshots").
/// </summary>
public sealed class SnapshotCalculator(IPriceRepository prices, IPricingConstants constants) : ICostCalculator
{
    private readonly IPriceRepository _prices = prices;
    private readonly IPricingConstants _constants = constants;

    public IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId)
    {
        var results = new List<CostResult>();
        var hours = _constants.HoursPerMonth();
        // Memoización por invocación: GetSnapshotPricePerGb hace un round-trip a SQL por llamada
        // (fresh-check + query de cache "Storage" completa); muchos snapshots comparten
        // (región, sku) — un cliente real ya tiene 184, miles son plausibles.
        var priceCache = new Dictionary<(string Region, string? Sku), double?>();

        foreach (var r in resources)
        {
            var result = new CostResult(r.ResourceId, analysisId, "snapshots");

            var sku = r.GetString("snapshot_sku") ?? r.GetString("sku_name");
            var sizeGb = r.GetInt("disk_size_gb");
            var region = PriceSelectors.NormalizeRegion(r.GetString("location"));
            var incremental = r.GetBool("incremental");

            if (sizeGb is null or 0 || string.IsNullOrEmpty(region))
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = $"Snapshot sin tamaño o región (sku={sku}, size={sizeGb})";
                results.Add(result);
                continue;
            }

            double? perGb;
            var key = (region, sku);
            if (priceCache.TryGetValue(key, out var cachedPerGb))
            {
                perGb = cachedPerGb;
            }
            else
            {
                try
                {
                    perGb = _prices.GetSnapshotPricePerGb(region, sku);
                }
                catch (Exception ex)
                {
                    result.CalculationStatus = "price_not_found";
                    result.CalculationNotes = $"Price lookup error: {ex.GetType().Name}";
                    results.Add(result);
                    continue;
                }
                priceCache[key] = perGb;
            }

            if (perGb is null or <= 0)
            {
                // Nunca aceptar un precio $0 silencioso: null Y <= 0 son "no encontrado". Los
                // snapshots vienen del MISMO QueryCached("Storage", region) que trae, en la misma
                // consulta de región, productos reales con TODOS sus meters en $0.00 (ej. "Azure
                // Files Provisioned v2" — ver StorageFilesRetailFixture.md §1.6); sin esta guarda
                // un $0.00 pasaría como precio válido y emitiría payg_monthly = $0 "calculated".
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = $"No price for snapshot sku={sku} region={region}";
                results.Add(result);
                continue;
            }

            result.PaygMonthly = sizeGb.Value * perGb.Value;
            result.PaygHourly = result.PaygMonthly / hours;
            result.StorageMonthly = result.PaygMonthly;
            result.RiApplies = false;
            result.RiNotApplicableReason = "Los snapshots no tienen reserva";
            result.CalculationNotes =
                $"Costo referencial por tamaño del disco de origen ({sizeGb.Value} GB); "
                + "Azure factura por espacio ocupado, el real puede ser menor. "
                + $"sku={sku}{(incremental ? ", incremental" : "")}";
            results.Add(result);
        }

        return results;
    }
}
