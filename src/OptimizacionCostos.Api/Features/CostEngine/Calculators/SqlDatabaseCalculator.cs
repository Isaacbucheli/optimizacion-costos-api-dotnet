using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Calculadora de Azure SQL Database. Port 1:1 de
/// app/calculators/sql_database.py::SqlDatabaseCalculator (service_key "sql").
///
/// Reglas:
/// - DTU tier (Basic, Standard, Premium): precio fijo mensual por nivel
///   (price * 30 si la unidad es "por día", si no price tal cual).
/// - vCore tier:
///     - Serverless: cobro por uso, payg_monthly = precio_estimado * horas * vCores.
///       No aplica RI; se añade nota de "tope máximo".
///     - Provisioned: precio fijo. Hoy tampoco aplica RI (todas RI=False).
/// - Hyperscale: variante vCore. Mismo trato que Provisioned/Serverless.
/// - DataWarehouse (Synapse Dedicated SQL Pool): NO se procesa aquí; lo cubre la
///   calculadora 'synapse_dw' (se filtra con continue, sin agregar resultado).
/// - BD dentro de un Elastic Pool: el costo vive en el pool; se costea en $0 para
///   no doble-contar (no se consulta precio).
/// </summary>
public sealed class SqlDatabaseCalculator(IPriceRepository prices, IPricingConstants constants) : ICostCalculator
{
    private readonly IPriceRepository _prices = prices;
    private readonly IPricingConstants _constants = constants;

    /// <summary>Normaliza la región: minúsculas y sin espacios. Port de _normalize_region.</summary>
    private static string NormalizeRegion(string? location)
    {
        if (string.IsNullOrEmpty(location)) return "";
        return location.ToLowerInvariant().Replace(" ", "");
    }

    /// <summary>Detecta SQL serverless por el nombre de SKU. Port de _is_serverless.</summary>
    private static bool IsServerless(string? skuName)
    {
        if (string.IsNullOrEmpty(skuName)) return false;
        return skuName.Contains("_S_") || skuName.EndsWith("_S") || skuName.Contains("Serverless");
    }

    public IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId)
    {
        var results = new List<CostResult>();
        var hours = _constants.HoursPerMonth();

        foreach (var r in resources)
        {
            var result = new CostResult(r.ResourceId, analysisId, "sql");

            // r.get("sku_name") or "" / r.get("sku_tier") or "" / r.get("compute_tier") or ""
            var skuName = r.GetString("sku_name") ?? "";
            var skuTier = r.GetString("sku_tier") ?? "";
            // r.get("sku_capacity") or 0 (truthiness: null/0 -> 0)
            var skuCapacity = r.GetInt("sku_capacity") ?? 0;
            var location = r.GetString("location");
            var computeTier = r.GetString("compute_tier") ?? "";

            var region = NormalizeRegion(location);
            if (string.IsNullOrEmpty(skuName) || string.IsNullOrEmpty(region))
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = "Missing sku_name or location";
                results.Add(result);
                continue;
            }

            // Synapse Dedicated SQL Pool: lo procesa el servicio 'synapse_dw'
            if (skuTier == "DataWarehouse")
            {
                continue;
            }

            // BD dentro de un Elastic Pool: el costo vive en el pool (servicio
            // 'elastic_pool'). Se costea en $0 para no doble-contar.
            var elasticPoolName = r.GetString("elastic_pool_name");
            if (!string.IsNullOrEmpty(elasticPoolName))
            {
                result.PaygMonthly = 0.0;
                result.PaygHourly = 0.0;
                result.RiApplies = false;
                result.RiNotApplicableReason = "BD dentro de Elastic Pool";
                result.CalculationNotes =
                    $"BD en Elastic Pool '{elasticPoolName}': $0 (costo incluido en el pool).";
                results.Add(result);
                continue;
            }

            SqlDbPriceDetails? priceInfo;
            try
            {
                priceInfo = _prices.GetSqlDbPriceDetails(skuName, region, skuTier, computeTier);
            }
            catch (Exception ex)
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = $"Price lookup error: {ex.GetType().Name}";
                results.Add(result);
                continue;
            }

            if (priceInfo is null || priceInfo.RetailPrice is null)
            {
                result.CalculationStatus = "price_not_found";
                result.CalculationNotes = $"No price for sku={skuName} region={region}";
                results.Add(result);
                continue;
            }

            var price = priceInfo.RetailPrice.Value;
            var unit = (priceInfo.UnitOfMeasure ?? "").ToLowerInvariant();

            var isDtuTier = skuTier is "Basic" or "Standard" or "Premium";
            var isServerless = IsServerless(skuName) || computeTier.Contains("Serverless");

            if (isDtuTier)
            {
                result.PaygMonthly = unit.Contains("day") ? price * 30.0 : price;
            }
            else
            {
                // vcores = sku_capacity or 1 (truthiness: 0 -> 1)
                var vcores = skuCapacity != 0 ? skuCapacity : 1;
                result.PaygMonthly = price * hours * vcores;
            }

            result.PaygHourly = result.PaygMonthly / hours;

            // Aplicabilidad RI
            if (isServerless)
            {
                result.RiApplies = false;
                result.RiNotApplicableReason = "Serverless tier";
            }
            else
            {
                result.RiApplies = false;    // En BANISI todas son Serverless; ajustar si aparecen Provisioned
                result.RiNotApplicableReason = "RI requiere análisis específico para SQL Provisioned";
            }

            result.CalculationNotes =
                $"sku={skuName} tier={skuTier} compute_tier={computeTier} " +
                $"capacity={skuCapacity} meter={priceInfo.MeterName} unit={priceInfo.UnitOfMeasure}";

            // Si el precio lo resolvió la IA, marcarlo en las notas → el front muestra "IA asistida".
            if (priceInfo.MatchStrategy is not null)
                result.CalculationNotes += $"; {priceInfo.MatchStrategy}";

            // Serverless factura por uso real con autopausa: el monto calculado es
            // un TOPE MÁXIMO (máx vCores × 730h encendido 24/7). El costo real será menor.
            if (isServerless)
            {
                result.CalculationNotes +=
                    "; NOTA: Serverless se factura por uso real (autopausa). " +
                    "El monto es un tope máximo (máx vCores × 730h); el costo real será menor.";
            }

            results.Add(result);
        }

        return results;
    }
}
