using System.Text.Json;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Calculadora de Synapse Dedicated SQL Pool (ex Azure SQL Data Warehouse).
/// Port 1:1 de app/calculators/synapse_dedicated_pool.py::SynapseDedicatedPoolCalculator.
///
/// Comparte azure_resource_type y tabla detalle (sql_db_details) con Azure SQL
/// Database; el loader carga todas las BDs del análisis, así que esta calculadora
/// solo procesa filas con sku_tier == "DataWarehouse" (y la de SQL las omite).
///
/// Reglas:
/// - Facturación por nivel DWc (DW100c..DW30000c) por hora. El nivel real viene
///   de properties.currentServiceObjectiveName (el sku_capacity de ARM no es confiable).
/// - Pool Paused: compute = $0 (status "not_running"); el storage no se incluye.
/// - RI 1Y/3Y: publicadas por bloque de 100 DWUs, ya vienen escaladas por bloques.
/// </summary>
public sealed class SynapseDedicatedPoolCalculator : ICostCalculator
{
    private readonly IPriceRepository _prices;
    private readonly IPricingConstants _constants;

    public SynapseDedicatedPoolCalculator(IPriceRepository prices, IPricingConstants constants)
    {
        _prices = prices ?? throw new ArgumentNullException(nameof(prices));
        _constants = constants ?? throw new ArgumentNullException(nameof(constants));
    }

    public IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId)
    {
        var results = new List<CostResult>();
        var hours = _constants.HoursPerMonth();

        foreach (var r in resources)
        {
            // Solo procesa filas de Synapse Dedicated SQL Pool; el resto las omite.
            if ((r.GetString("sku_tier") ?? "") != "DataWarehouse")
            {
                continue;
            }

            var result = new CostResult(r.ResourceId, analysisId, "synapse_dw");

            var region = NormalizeRegion(r.GetString("location"));
            if (string.IsNullOrEmpty(region))
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = "Missing location";
                results.Add(result);
                continue;
            }

            var props = ParseProperties(r);
            // Paridad con Python `a or b or ""`: la cadena VACÍA también cae al
            // fallback (no solo null). Por eso usamos FirstNonEmpty, no `??`.
            var dwLevel = FirstNonEmpty(
                GetStringProp(props, "currentServiceObjectiveName"),
                GetStringProp(props, "requestedServiceObjectiveName"));
            var status = (GetStringProp(props, "status") ?? "").ToLowerInvariant();

            if (string.IsNullOrEmpty(dwLevel))
            {
                result.CalculationStatus = "manual_required";
                result.CalculationNotes =
                    "Synapse Dedicated SQL Pool sin nivel DWc en properties "
                    + "(currentServiceObjectiveName). Requiere validacion manual.";
                results.Add(result);
                continue;
            }

            if (status == "paused")
            {
                result.PaygMonthly = 0.0;
                result.CalculationStatus = "not_running";
                result.RiApplies = false;
                result.RiNotApplicableReason = "Pool pausado";
                result.CalculationNotes =
                    $"Synapse DW pausado: compute $0 (storage no incluido). nivel={dwLevel}";
                results.Add(result);
                continue;
            }

            SynapseDwPrices priceData;
            try
            {
                priceData = _prices.GetSynapseDwPrices(dwLevel, region);
            }
            catch (Exception ex)
            {
                // No exponer el detalle del error; solo el tipo (como el log de Python).
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = $"Price lookup error: {ex.GetType().Name}";
                results.Add(result);
                continue;
            }

            var paygHourly = priceData.PaygHourly;
            if (paygHourly is null)
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes =
                    $"No price for Synapse DW nivel={dwLevel} region={region}";
                results.Add(result);
                continue;
            }

            result.PaygHourly = paygHourly.Value;
            result.PaygMonthly = paygHourly.Value * hours;
            result.PaygMeterId = priceData.PaygMeterId;

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
            result.RiApplies = ri1yTotal is not null || ri3yTotal is not null;
            if (!result.RiApplies)
            {
                result.RiNotApplicableReason = "Sin reservas publicadas para el nivel";
            }
            result.ComputeSavings();
            result.DiscardNonSavingRi();

            result.CalculationNotes =
                $"Synapse Dedicated SQL Pool nivel={dwLevel} region={region} "
                + "(storage no incluido)";
            results.Add(result);
        }

        return results;
    }

    /// <summary>Normaliza la región: minúsculas y sin espacios (igual que el _normalize_region de Python).</summary>
    private static string NormalizeRegion(string? location)
    {
        if (string.IsNullOrEmpty(location))
        {
            return "";
        }
        return location.ToLowerInvariant().Replace(" ", "");
    }

    /// <summary>
    /// Parsea properties_json a un diccionario. Devuelve un dict vacío si está ausente,
    /// vacío o no es un objeto JSON (equivale a _parse_properties de Python).
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement> ParseProperties(ResourceRow resource)
    {
        var raw = resource.GetString("properties_json");
        if (string.IsNullOrEmpty(raw))
        {
            return EmptyProps;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return EmptyProps;
            }
            var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Clonar para que el valor sobreviva al dispose del documento.
                dict[prop.Name] = prop.Value.Clone();
            }
            return dict;
        }
        catch (JsonException)
        {
            return EmptyProps;
        }
    }

    /// <summary>Primer valor no nulo y no vacío (equivale a `a or b or ""` de Python).</summary>
    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v))
            {
                return v;
            }
        }
        return "";
    }

    /// <summary>Lee una propiedad string del dict parseado; null si ausente o no es cadena.</summary>
    private static string? GetStringProp(IReadOnlyDictionary<string, JsonElement> props, string key)
    {
        if (!props.TryGetValue(key, out var el))
        {
            return null;
        }
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyProps =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}
