namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Servicio "public_ip": port de <c>get_public_ip_monthly_price</c> y su selector
/// <c>_select_public_ip_monthly_price</c> de app/pricing/price_repository.py.
///
/// Peculiaridades portadas 1:1:
///   - La región se NORMALIZA internamente (lower sin espacios) antes de cachear/fetch;
///     si queda vacía, devuelve null sin tocar cache (igual que Python).
///   - El meter de la Retail API es HORARIO ("1 Hour"); el monto mensual = precio × 730.
///     Si el meter ya viniera mensual ("month"), se devuelve tal cual. Cualquier otra
///     unidad → null.
///   - Solo cuenta el producto "IP Addresses" (descarta "Public IP Prefix"), Consumption,
///     sku exacto (lower), y meter que contenga el método de asignación y "public ip".
///   - El fallback de IA NO se porta: si el determinista no halla, devuelve null.
/// </summary>
public sealed partial class SqlPriceRepository
{
    /// <summary>Python: <c>get_public_ip_monthly_price</c>.</summary>
    public double? GetPublicIpMonthlyPrice(string skuName, string region, string allocationMethod = "Static")
    {
        const string serviceName = "Virtual Network";
        var normalizedRegion = PriceSelectors.NormalizeRegion(region);
        if (string.IsNullOrEmpty(normalizedRegion))
            return null;

        var fetchQuery = $"public_ip {normalizedRegion}";
        RefreshByFetchQueryIfStale(
            fetchQuery,
            () => _client.FetchPublicIpPrices(normalizedRegion));

        var cached = _cache.QueryCached(serviceName, normalizedRegion);
        return SelectPublicIpMonthlyPrice(cached, skuName, allocationMethod);
    }

    /// <summary>
    /// Python: <c>_select_public_ip_monthly_price(cached, sku_name, allocation_method)</c>.
    /// </summary>
    private static double? SelectPublicIpMonthlyPrice(
        IReadOnlyList<PriceRow> cached,
        string skuName,
        string allocationMethod = "Static")
    {
        var sku = (string.IsNullOrEmpty(skuName) ? "Basic" : skuName).ToLowerInvariant();
        var allocation = (string.IsNullOrEmpty(allocationMethod) ? "Static" : allocationMethod).ToLowerInvariant();

        var candidates = cached
            .Where(c =>
                (c.ProductName ?? string.Empty) == "IP Addresses"
                && PriceSelectors.IsConsumption(c)
                && (c.SkuName ?? string.Empty).ToLowerInvariant() == sku
                && (c.MeterName ?? string.Empty).ToLowerInvariant().Contains(allocation)
                && (c.MeterName ?? string.Empty).ToLowerInvariant().Contains("public ip"))
            .ToList();

        var item = PriceSelectors.PreferPrimaryHourly(candidates);
        if (item is null)
            return null;

        var unit = (item.UnitOfMeasure ?? string.Empty).ToLowerInvariant();
        var price = item.RetailPrice;
        if (price is null)
            return null;
        if (unit.Contains("hour"))
            return price.Value * 730.0;
        if (unit.Contains("month"))
            return price.Value;
        return null;
    }
}
