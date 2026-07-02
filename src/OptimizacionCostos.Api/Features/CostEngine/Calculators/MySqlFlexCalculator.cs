using System.Globalization;
using System.Text.RegularExpressions;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Calculadora de MySQL Flexible Server. Port 1:1 de
/// app/calculators/mysql_flex.py::MySqlFlexCalculator (service_key "mysql").
///
/// Reglas:
/// - compute_monthly = precio_payg_hourly × horas/mes × vcores.
/// - storage_monthly = precio_storage_per_gb × storage_size_gb.
/// - payg_monthly = compute_monthly + storage_monthly.
/// - RI: aplica en GeneralPurpose y MemoryOptimized. NO en Burstable.
/// </summary>
public sealed partial class MySqlFlexCalculator : ICostCalculator
{
    private readonly IPriceRepository _prices;
    private readonly IPricingConstants _constants;

    public MySqlFlexCalculator(IPriceRepository prices, IPricingConstants constants)
    {
        _prices = prices;
        _constants = constants;
    }

    // Regex para derivar conteo de vCores del nombre del SKU: _([A-Za-z]+)?(\d+) -> grupo 1 (dígitos).
    [GeneratedRegex(@"_(?:[A-Za-z]+)?(\d+)")]
    private static partial Regex VcoreRegex();

    private static string NormalizeRegion(string? location)
    {
        if (string.IsNullOrEmpty(location)) return "";
        return location.ToLowerInvariant().Replace(" ", "");
    }

    private static int VcoreCountFromSku(string? skuName)
    {
        var match = VcoreRegex().Match(skuName ?? "");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
    }

    public IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId)
    {
        var results = new List<CostResult>();
        var hours = _constants.HoursPerMonth();

        foreach (var r in resources)
        {
            var result = new CostResult(r.ResourceId, analysisId, "mysql");

            var skuName = r.GetString("sku_name") ?? "";
            var skuTier = r.GetString("sku_tier") ?? "";
            // r.get("storage_size_gb") or 0 -> truthiness Python: null/0/"" => 0.
            var storageSizeGb = r.GetDouble("storage_size_gb") ?? 0.0;
            var location = r.GetString("location");

            var region = NormalizeRegion(location);
            if (string.IsNullOrEmpty(skuName) || string.IsNullOrEmpty(region))
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = "Missing sku_name or location";
                results.Add(result);
                continue;
            }

            MySqlFlexPrices priceData;
            double? storagePerGb;
            try
            {
                priceData = _prices.GetMySqlFlexPrices(skuName, region, skuTier);
                storagePerGb = _prices.GetMySqlStoragePricePerGb(region);
            }
            catch (Exception ex)
            {
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
                    $"No hubo candidato real en Azure Retail Prices API para MySQL Flexible Server " +
                    $"sku={skuName} tier={skuTier} region={region}. Requiere validacion manual.";
                results.Add(result);
                continue;
            }

            var vcores = priceData.IsPerVcore ? VcoreCountFromSku(skuName) : 1;
            var computeMonthly = paygHourly.Value * hours * vcores;
            // (storage_per_gb * storage_size_gb) if storage_per_gb else 0.0 -> truthiness: storage_per_gb 0/null => 0.0.
            var storageMonthly = (storagePerGb is { } spg && spg != 0.0) ? spg * storageSizeGb : 0.0;

            result.PaygHourly = paygHourly.Value;
            result.PaygMonthly = computeMonthly + storageMonthly;
            result.StorageMonthly = storageMonthly > 0 ? storageMonthly : null;
            result.PaygMeterId = priceData.PaygMeterId;

            // RI: NO aplica en Burstable
            if (skuTier.ToLowerInvariant() == "burstable")
            {
                result.RiApplies = false;
                result.RiNotApplicableReason = "Burstable tier no soporta RI";
            }
            else
            {
                result.RiApplies = true;
                var ri1yTotal = priceData.Ri1yTotal;
                var ri3yTotal = priceData.Ri3yTotal;
                if (ri1yTotal is not null)
                {
                    result.Ri1yMonthly = ((ri1yTotal.Value * vcores) / 12.0) + storageMonthly;
                }
                if (ri3yTotal is not null)
                {
                    result.Ri3yMonthly = ((ri3yTotal.Value * vcores) / 36.0) + storageMonthly;
                }
                result.ComputeSavings();
                result.DiscardNonSavingRi();
            }

            var matchStrategy = priceData.MatchStrategy ?? "deterministic";
            result.CalculationNotes =
                $"sku={skuName} tier={skuTier} region={region} " +
                $"vcores={vcores} storage_gb={FormatGb(storageSizeGb)} compute=${computeMonthly.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"storage=${storageMonthly.ToString("F2", CultureInfo.InvariantCulture)} match={matchStrategy}";
            results.Add(result);
        }

        return results;
    }

    // El Python interpola storage_size_gb tal cual (int 128 => "128"). Emulamos: enteros sin decimales.
    private static string FormatGb(double gb)
        => gb == Math.Floor(gb) && !double.IsInfinity(gb)
            ? ((long)gb).ToString(CultureInfo.InvariantCulture)
            : gb.ToString(CultureInfo.InvariantCulture);
}
