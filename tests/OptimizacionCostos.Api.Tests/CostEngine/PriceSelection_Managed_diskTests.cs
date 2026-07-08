using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Tests de SELECCIÓN de precio para Managed Disk (<see cref="SqlPriceRepository.GetDiskPrice"/>).
/// Port 1:1 de tests/test_paas_pricing.py::test_disk_price_uses_exact_disk_meter_not_mount_or_operations.
/// Los arrays de precios y el monto esperado son EXACTAMENTE los del test Python (no recalculados).
/// </summary>
public sealed class PriceSelection_Managed_diskTests
{
    private static SqlPriceRepository NewRepo(FakePriceCache cache, FakeRetailPriceClient client)
        => new(cache, client, new FakePricingConstants());

    /// <summary>
    /// Port de test_disk_price_uses_exact_disk_meter_not_mount_or_operations.
    /// Regresión real: P6/E10 tomaban "Disk Mount" u "Operations" según orden; debe elegir
    /// el meter EXACTO "P6 LRS Disk" = 9.28, NO 0.47 (Mount) ni 0.002 (Operations).
    /// Equivale a patch _is_cache_fresh_for=True + _query_cached=cached.
    /// </summary>
    [Fact]
    public void GetDiskPrice_UsesExactDiskMeter_NotMountOrOperations()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Premium SSD Managed Disks"),
                ("sku_name", "P6 LRS"),
                ("meter_name", "P6 LRS Disk Mount"),
                ("price_type", "Consumption"),
                ("retail_price", 0.47),
            },
            new (string, object?)[]
            {
                ("product_name", "Premium SSD Managed Disks"),
                ("sku_name", "P6 LRS"),
                ("meter_name", "P6 LRS Disk Operations"),
                ("price_type", "Consumption"),
                ("retail_price", 0.002),
            },
            new (string, object?)[]
            {
                ("product_name", "Premium SSD Managed Disks"),
                ("sku_name", "P6 LRS"),
                ("meter_name", "P6 LRS Disk"),
                ("price_type", "Consumption"),
                ("retail_price", 9.28),
            });

        var cache = new FakePriceCache
        {
            IsCacheFreshForFn = (_, _, _, _) => true, // cache fresca: no se dispara fetch
            QueryCachedFn = (_, _, _, _) => cached,
        };
        var client = new FakeRetailPriceClient();
        var repo = NewRepo(cache, client);

        var price = repo.GetDiskPrice("P6 LRS", "eastus2");

        Assert.Equal(9.28, price);

        // Cache fresca => no hubo fetch/insert/delete (paridad con el patch _is_cache_fresh_for=True).
        Assert.Empty(client.FetchManagedDiskPricesCalls);
        Assert.Equal(0, cache.InsertCallCount);
        Assert.Equal(0, cache.DeleteStaleCallCount);
    }
}
