namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Precio/GB de snapshot de managed disk. Net-new (Fase 2, FinOps Toolkit): los snapshots se
/// facturan por GB/mes según el tipo de storage del snapshot (Standard HDD por defecto; Premium
/// SSD si el sku es Premium_*). Meters reales: product "Standard HDD Managed Disks"/"Premium SSD
/// Managed Disks", meter "{LRS|ZRS|GRS} Snapshots", unidad "1 GB/Month".
/// </summary>
public sealed partial class SqlPriceRepository
{
    public double? GetSnapshotPricePerGb(string region, string? storageType)
    {
        const string serviceName = "Storage";
        RefreshByFetchQueryIfStale($"snapshots {region}", () => _client.FetchSnapshotPrices(region));
        var cached = _cache.QueryCached(serviceName, region);

        var (product, meter) = SnapshotMeterFor(storageType);
        var item = cached.FirstOrDefault(c =>
            c.PriceType == "Consumption"
            && string.Equals(c.ProductName, product, StringComparison.Ordinal)
            && string.Equals(c.MeterName, meter, StringComparison.Ordinal)
            && (c.UnitOfMeasure ?? string.Empty).StartsWith("1 GB/Month", StringComparison.OrdinalIgnoreCase));
        return item?.RetailPrice;
    }

    /// <summary>sku.name del snapshot → (product, meter) del Retail API. Default Standard HDD LRS.</summary>
    private static (string Product, string Meter) SnapshotMeterFor(string? storageType)
    {
        var t = (storageType ?? string.Empty).ToUpperInvariant();
        var product = t.StartsWith("PREMIUM", StringComparison.Ordinal)
            ? "Premium SSD Managed Disks"
            : "Standard HDD Managed Disks";
        var redundancy = t.Contains("ZRS", StringComparison.Ordinal) ? "ZRS"
            : t.Contains("GRS", StringComparison.Ordinal) ? "GRS"
            : "LRS";
        return (product, $"{redundancy} Snapshots");
    }
}
