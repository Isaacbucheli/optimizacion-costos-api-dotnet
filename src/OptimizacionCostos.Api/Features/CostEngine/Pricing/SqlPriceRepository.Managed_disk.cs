namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Precio de Managed Disk. Port 1:1 de <c>get_disk_price</c> de
/// app/pricing/price_repository.py (servicio del CalculatorRegistry: <c>managed_disk</c> / <c>disk</c>).
///
/// Peculiaridad clave (regresión MINSUR): cada tier trae varios meters
/// ("P6 LRS Disk", "P6 LRS Disk Mount", "P6 LRS Disk Operations"); SOLO el meter EXACTO
/// "&lt;tier&gt; Disk" (lower) es el precio mensual del disco. NO se debe tomar "Disk Mount"
/// ni "Operations" según el orden de la lista.
/// </summary>
public sealed partial class SqlPriceRepository
{
    /// <summary>
    /// Port de <c>get_disk_price(sku_tier, region)</c>. sku_tier ej: "E10 LRS", "P10 LRS", "S10 LRS".
    /// Servicio "Storage"; freshness parametrizado por sku_name (FIX v0.5.3).
    /// Devuelve el retail_price mensual del disco o null si no hay match.
    /// </summary>
    public double? GetDiskPrice(string skuTier, string region)
    {
        const string serviceName = "Storage";

        // refresh si rancio: parametrizado por sku_name = skuTier.
        RefreshIfStale(
            serviceName,
            region,
            () => _client.FetchManagedDiskPrices(skuTier, region),
            $"disk {skuTier} {region}",
            skuName: skuTier);

        var cached = _cache.QueryCached(serviceName, region, skuName: skuTier);

        // Solo filas de "Managed Disks".
        // (Python: [c for c in cached if "Managed Disks" in (c.get("product_name") or "")])
        // Python usa el operador "in" → substring CASE-SENSITIVE. Replicamos con
        // Contains(..., StringComparison.Ordinal) para igualar el comportamiento exacto del
        // origen (PriceSelectors.Contains sería case-insensitive y divergiría).
        var managed = cached
            .Where(c => (c.ProductName ?? string.Empty).Contains("Managed Disks", StringComparison.Ordinal))
            .ToList();

        // Solo "<tier> Disk" (lower) es el precio mensual del disco; NO "Disk Mount"/"Operations".
        var expectedMeter = $"{skuTier} Disk".ToLowerInvariant();
        var item = managed.FirstOrDefault(c =>
            c.PriceType == "Consumption"
            && c.MeterNameLower == expectedMeter);

        return item?.RetailPrice;
    }
}
