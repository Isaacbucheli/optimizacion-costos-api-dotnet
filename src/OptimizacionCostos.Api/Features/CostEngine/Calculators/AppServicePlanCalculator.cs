using System.Globalization;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Calculadora de App Service Plans. Port 1:1 de
/// app/calculators/app_service_plan.py::AppServicePlanCalculator (service_key "appservice").
///
/// Reglas:
/// - B*, S*, P* (instancias dedicadas): precio fijo por hora × 730 × capacity.
/// - EP* (Functions Elastic Premium) / WS* (Logic Apps Standard): se facturan por
///   vCPU/hora + RAM/hora. Se costea el BASE de instancia reservada (vCPU+RAM 24/7);
///   el excedente variable por ejecuciones NO se incluye.
/// - Y1 (Consumption Functions), FC1 (Flex Consumption), F1 (Free): variable.
///   → calculation_status = "variable_pricing", payg_monthly = null.
/// - Premium V3 (P0v3, P1v3, P2v3, P3v3, P*mv3): aplica RI.
/// - El resto: no aplica RI.
/// </summary>
public sealed class AppServicePlanCalculator : ICostCalculator
{
    // SKUs de consumo variable (Functions Consumption / Flex Consumption / Free).
    private static readonly HashSet<string> VariableSkus = new(StringComparer.Ordinal)
    {
        "Y1", "FC1", "F1",
    };

    // EP* (Functions Elastic Premium) y WS* (Logic Apps Standard) tienen base fija
    // de instancia reservada → se costean aparte (no son consumo puro).
    private static readonly string[] ElasticPremiumPrefixes = { "EP", "WS" };

    // Premium V3 (únicos SKUs de App Service con RI). Comparación en minúsculas.
    private static readonly HashSet<string> PremiumV3SkusNormalized = new(StringComparer.Ordinal)
    {
        "p0v3", "p1v3", "p2v3", "p3v3",
        "p1mv3", "p2mv3", "p3mv3", "p4mv3", "p5mv3",
    };

    private readonly IPriceRepository _prices;
    private readonly IPricingConstants _constants;

    public AppServicePlanCalculator(IPriceRepository prices, IPricingConstants constants)
    {
        _prices = prices ?? throw new ArgumentNullException(nameof(prices));
        _constants = constants ?? throw new ArgumentNullException(nameof(constants));
    }

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
            var result = new CostResult(r.ResourceId, analysisId, "appservice");

            // r.get("sku_name") or "" -> "" si ausente/null.
            var skuName = r.GetString("sku_name") ?? "";
            // r.get("sku_capacity") or 1 -> 1 si ausente/null/0 (truthy estilo Python).
            var skuCapacity = r.GetInt("sku_capacity") is { } cap && cap != 0 ? cap : 1;
            var isLinux = r.GetBool("is_linux");
            var location = r.GetString("location");

            // Consumo variable.
            if (VariableSkus.Contains(skuName))
            {
                result.CalculationStatus = "variable_pricing";
                result.IsVariablePricing = true;
                result.RiApplies = false;
                result.RiNotApplicableReason = "Consumo variable";
                result.CalculationNotes = $"SKU {skuName}: consumo variable";
                results.Add(result);
                continue;
            }

            var region = NormalizeRegion(location);
            if (string.IsNullOrEmpty(skuName) || string.IsNullOrEmpty(region))
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = "Missing sku_name or location";
                results.Add(result);
                continue;
            }

            // Elastic Premium (EP*) / Workflow Standard (WS*): base reservada vCPU+RAM.
            if (StartsWithAny(skuName.ToUpperInvariant(), ElasticPremiumPrefixes))
            {
                ElasticPremiumBase? @base;
                try
                {
                    @base = _prices.GetElasticPremiumBasePrice(skuName, region);
                }
                catch
                {
                    // Falla del lookup de precios: tratar como sin base (igual que el except de Python).
                    @base = null;
                }

                if (@base is null)
                {
                    result.CalculationStatus = "manual_required";
                    result.CalculationNotes =
                        $"No se hallaron precios de duración vCPU/RAM para {skuName} " +
                        $"region={region}. Requiere validación manual.";
                    results.Add(result);
                    continue;
                }

                result.PaygHourly = @base.BaseHourly;
                result.PaygMonthly = @base.BaseHourly * hours * skuCapacity;
                result.RiApplies = false;
                result.RiNotApplicableReason = "Plan de consumo (sin RI); solo base reservada";
                result.CalculationNotes =
                    $"sku={skuName} region={region} capacity={skuCapacity} " +
                    $"base={Fmt(@base.Vcpu)}vCPU+{Fmt(@base.Gb)}GB " +
                    $"(vCPU/h={Fmt(@base.VcpuHourly)} RAM_GiB/h={Fmt(@base.MemGibHourly)}); " +
                    "NOTA: solo base de instancia reservada; el excedente variable " +
                    "por ejecuciones/consumo NO está incluido.";
                results.Add(result);
                continue;
            }

            AppServicePrices priceData;
            try
            {
                priceData = _prices.GetAppServicePrices(skuName, region, isLinux);
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
                    $"No hubo candidato real en Azure Retail Prices API para App Service " +
                    $"sku={skuName} region={region} linux={PyBool(isLinux)}. Requiere validacion manual.";
                results.Add(result);
                continue;
            }

            result.PaygHourly = paygHourly.Value;
            result.PaygMonthly = paygHourly.Value * hours * skuCapacity;

            // RI solo en Premium V3.
            if (PremiumV3SkusNormalized.Contains(skuName.ToLowerInvariant()))
            {
                result.RiApplies = true;
                var ri1yTotal = priceData.Ri1yTotal;
                var ri3yTotal = priceData.Ri3yTotal;
                if (ri1yTotal is not null)
                {
                    result.Ri1yMonthly = (ri1yTotal.Value / 12.0) * skuCapacity;
                }
                if (ri3yTotal is not null)
                {
                    result.Ri3yMonthly = (ri3yTotal.Value / 36.0) * skuCapacity;
                }
                result.ComputeSavings();
                result.DiscardNonSavingRi();
            }
            else
            {
                result.RiApplies = false;
                result.RiNotApplicableReason = $"SKU {skuName} no es Premium V3";
            }

            var matchStrategy = priceData.MatchStrategy;
            var matchLabel = string.IsNullOrEmpty(matchStrategy) ? "deterministic" : matchStrategy;
            result.CalculationNotes =
                $"sku={skuName} region={region} linux={PyBool(isLinux)} capacity={skuCapacity} " +
                $"match={matchLabel}";
            results.Add(result);
        }

        return results;
    }

    private static bool StartsWithAny(string value, string[] prefixes)
    {
        foreach (var p in prefixes)
        {
            if (value.StartsWith(p, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // Representación estilo Python bool en str (True/False) para las notas.
    private static string PyBool(bool value) => value ? "True" : "False";

    // Representación numérica invariante para las notas (sin separadores de cultura).
    private static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Fmt(int value) => value.ToString(CultureInfo.InvariantCulture);
}
