using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Calculadora de costo de Azure SQL Managed Instance. Port 1:1 de
/// app/calculators/sql_managed_instance.py (clase SqlManagedInstanceCalculator,
/// service_key "sql_managed_instance").
///
/// Peculiaridades portadas con paridad exacta:
///   - compute = payg_hourly_per_vcore * horas_mes * vcores.
///   - storage = storage_monthly_per_gb * storage_gb (NO se descuenta de la RI).
///   - payg_monthly = compute + storage.
///   - payg_hourly = payg_hourly_per_vcore * vcores.
///   - ri_1y = (ri_1y_total_per_vcore * vcores / 12) + storage.
///   - ri_3y = (ri_3y_total_per_vcore * vcores / 36) + storage.
///   - storage_monthly se setea solo si &gt; 0 (si no, queda null).
///   - zone_redundant se pasa al lookup de precios.
/// Estados: price_not_found (faltan datos / error de lookup),
/// manual_required (no hay precio de compute), calculated (con/sin RI).
/// </summary>
public sealed class SqlManagedInstanceCalculator(IPriceRepository prices, IPricingConstants constants) : ICostCalculator
{
    private readonly IPriceRepository _prices = prices;
    private readonly IPricingConstants _constants = constants;

    public IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId)
    {
        var results = new List<CostResult>();
        var hours = _constants.HoursPerMonth();

        foreach (var resource in resources)
        {
            var result = new CostResult(resource.ResourceId, analysisId, "sql_managed_instance");

            var region = NormalizeRegion(resource.GetString("location"));
            var skuTier = resource.GetString("sku_tier") ?? "";
            var skuFamily = resource.GetString("sku_family") ?? "";
            // _as_int(value, default=1): default 1 si falta/no parseable.
            var vcores = resource.GetInt("vcore_count") ?? 1;
            // _as_int(value, default=0): default 0 si falta/no parseable.
            var storageGb = resource.GetInt("storage_size_gb") ?? 0;
            var licenseType = resource.GetString("license_type") ?? "";
            var zoneRedundant = resource.GetBool("zone_redundant");

            // Python: if not region or not sku_tier or not vcores.
            // vcores es falsy solo si es 0 (cadenas vacías cubren region/tier).
            if (string.IsNullOrEmpty(region) || string.IsNullOrEmpty(skuTier) || vcores == 0)
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes =
                    $"Faltan datos para calcular SQL MI: region={region} " +
                    $"tier={skuTier} vcores={vcores}";
                results.Add(result);
                continue;
            }

            SqlManagedInstancePrices price;
            try
            {
                price = _prices.GetSqlManagedInstancePrices(
                    region, skuTier, skuFamily, vcores, zoneRedundant);
            }
            catch (Exception ex)
            {
                // No exponemos el detalle: solo el nombre del tipo (sin secretos).
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = $"Error consultando precios SQL MI: {ex.GetType().Name}";
                results.Add(result);
                continue;
            }

            var computeHourly = price.PaygHourlyPerVcore;
            var storagePerGb = price.StorageMonthlyPerGb ?? 0;
            if (computeHourly is null)
            {
                result.CalculationStatus = "manual_required";
                result.CalculationNotes =
                    $"No hubo candidato real en Azure Retail Prices API para SQL Managed Instance " +
                    $"tier={skuTier} family={skuFamily} region={region}. Requiere validacion manual.";
                results.Add(result);
                continue;
            }

            var computeMonthly = computeHourly.Value * hours * vcores;
            var storageMonthly = storagePerGb * storageGb;
            result.PaygHourly = computeHourly.Value * vcores;
            result.PaygMonthly = computeMonthly + storageMonthly;
            result.StorageMonthly = storageMonthly > 0 ? storageMonthly : null;
            result.PaygMeterId = price.PaygMeterId;

            var ri1y = price.Ri1yTotalPerVcore;
            var ri3y = price.Ri3yTotalPerVcore;
            if (ri1y is not null)
            {
                result.Ri1yMonthly = (ri1y.Value * vcores / 12) + storageMonthly;
            }
            if (ri3y is not null)
            {
                result.Ri3yMonthly = (ri3y.Value * vcores / 36) + storageMonthly;
            }

            // Python: bool(ri_1y or ri_3y) — verdadero si alguno es no-None y no 0.
            result.RiApplies = (ri1y is not null && ri1y.Value != 0)
                || (ri3y is not null && ri3y.Value != 0);
            if (!result.RiApplies)
            {
                result.RiNotApplicableReason = "No se encontro precio de reserva para el SKU";
            }
            result.ComputeSavings();
            result.DiscardNonSavingRi();
            result.CalculationNotes =
                $"tier={skuTier} family={(string.IsNullOrEmpty(skuFamily) ? "Gen5" : skuFamily)} vcores={vcores} " +
                $"storage_gb={storageGb} license={(string.IsNullOrEmpty(licenseType) ? "no informado" : licenseType)} " +
                $"zone_redundant={PyBool(zoneRedundant)} " +
                $"compute_meter={price.ComputeMeterName} compute_sku={price.ComputeSkuName} " +
                $"storage_meter={price.StorageMeterName} storage_sku={price.StorageSkuName} " +
                $"match={(string.IsNullOrEmpty(price.MatchStrategy) ? "deterministic" : price.MatchStrategy)}";
            results.Add(result);
        }

        return results;
    }

    private static string NormalizeRegion(string? location)
        => (location ?? "").ToLowerInvariant().Replace(" ", "");

    /// <summary>Representación estilo Python de un bool ("True"/"False") para las notas.</summary>
    private static string PyBool(bool value) => value ? "True" : "False";
}
