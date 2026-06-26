using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Helper para construir <see cref="PriceRow"/> desde los dicts literales de los tests
/// Python (test_paas_pricing.py). Acepta las mismas claves snake_case que esos dicts
/// (product_name, sku_name, meter_name, price_type, reservation_term, retail_price,
/// unit_of_measure, is_primary_meter, arm_sku_name, ...). Las claves ausentes quedan
/// en su default (null / false), igual que cuando el dict Python las omite.
///
/// Uso:
/// <code>
///   var row = PriceRowFactory.Of(
///       ("product_name", "Azure App Service Linux"),
///       ("sku_name", "P1v3"),
///       ("price_type", "Consumption"),
///       ("retail_price", 0.20),
///       ("unit_of_measure", "1 Hour"),
///       ("is_primary_meter", true));
/// </code>
///
/// O por named-args con <see cref="Make"/> cuando se prefiere legibilidad fuerte.
/// </summary>
public static class PriceRowFactory
{
    public static PriceRow Of(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
            d[k] = v;

        return new PriceRow
        {
            PriceId = AsLong(Get(d, "price_id")),
            ServiceName = AsString(Get(d, "service_name", "serviceName")),
            ProductName = AsString(Get(d, "product_name", "productName")),
            SkuName = AsString(Get(d, "sku_name", "skuName")),
            ArmSkuName = AsString(Get(d, "arm_sku_name", "armSkuName")),
            MeterName = AsString(Get(d, "meter_name", "meterName")),
            MeterId = AsString(Get(d, "meter_id", "meterId")),
            ArmRegionName = AsString(Get(d, "arm_region_name", "armRegionName")),
            PriceType = AsString(Get(d, "price_type", "type")),
            ReservationTerm = AsString(Get(d, "reservation_term", "reservationTerm")),
            CurrencyCode = AsString(Get(d, "currency_code", "currencyCode")),
            UnitPrice = AsDouble(Get(d, "unit_price", "unitPrice")),
            RetailPrice = AsDouble(Get(d, "retail_price", "retailPrice")),
            UnitOfMeasure = AsString(Get(d, "unit_of_measure", "unitOfMeasure")),
            TierMinimumUnits = AsDouble(Get(d, "tier_minimum_units", "tierMinimumUnits")),
            IsPrimaryMeter = AsBool(Get(d, "is_primary_meter", "isPrimaryMeter")),
            FetchQuery = AsString(Get(d, "fetch_query")),
            CachedAt = Get(d, "cached_at") as DateTime?,
        };
    }

    /// <summary>Convierte una lista de dicts (cada uno un array de pares) en filas.</summary>
    public static IReadOnlyList<PriceRow> Many(params (string Key, object? Value)[][] rows)
        => rows.Select(Of).ToList();

    /// <summary>
    /// Construcción por named-args, para tests nuevos que prefieren tipos fuertes en vez de
    /// claves Python. Todos opcionales con los mismos defaults que <see cref="Of"/>.
    /// </summary>
    public static PriceRow Make(
        string? productName = null,
        string? skuName = null,
        string? armSkuName = null,
        string? meterName = null,
        string? priceType = null,
        string? reservationTerm = null,
        double? retailPrice = null,
        string? unitOfMeasure = null,
        bool isPrimaryMeter = false,
        string? serviceName = null,
        string? armRegionName = null)
        => new PriceRow
        {
            ProductName = productName,
            SkuName = skuName,
            ArmSkuName = armSkuName,
            MeterName = meterName,
            PriceType = priceType,
            ReservationTerm = reservationTerm,
            RetailPrice = retailPrice,
            UnitOfMeasure = unitOfMeasure,
            IsPrimaryMeter = isPrimaryMeter,
            ServiceName = serviceName,
            ArmRegionName = armRegionName,
        };

    // -------------------- Coerción de tipos --------------------

    private static object? Get(IReadOnlyDictionary<string, object?> d, string snake, string? camel = null)
    {
        if (d.TryGetValue(snake, out var v))
            return v;
        if (camel is not null && d.TryGetValue(camel, out var vc))
            return vc;
        return null;
    }

    private static string? AsString(object? v) => v as string;

    private static bool AsBool(object? v) => v is bool b && b;

    private static double? AsDouble(object? v) => v switch
    {
        null => null,
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        decimal m => (double)m,
        _ => null,
    };

    private static long? AsLong(object? v) => v switch
    {
        null => null,
        long l => l,
        int i => i,
        _ => null,
    };
}
