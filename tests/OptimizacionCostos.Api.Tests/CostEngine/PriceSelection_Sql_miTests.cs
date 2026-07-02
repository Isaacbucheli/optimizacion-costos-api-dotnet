using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Port de los tests de SELECCIÓN de SQL Managed Instance de tests/test_paas_pricing.py
/// (test_sql_managed_instance_selection_*). Ejercitan la selección determinista vía el
/// método público <see cref="SqlPriceRepository.GetSqlManagedInstancePrices"/>, con la
/// caché devolviendo los arrays canned y fetch_query "fresco" (IsFetchQueryFresh=true) para
/// que NO se dispare el fetch (equivale a llamar directamente a _select_sql_mi_prices).
///
/// Montos esperados copiados EXACTAMENTE del Python; nunca recalculados.
/// </summary>
public sealed class PriceSelection_Sql_miTests
{
    private static SqlPriceRepository BuildRepo(FakePriceCache cache, FakeRetailPriceClient client)
        => new SqlPriceRepository(cache, client, new FakePricingConstants());

    // Python: test_sql_managed_instance_selection_uses_tier_family_and_storage
    [Fact]
    public void Selection_UsesTierFamilyAndStorage()
    {
        var cached = PriceRowFactory.Many(
            new[]
            {
                ("product_name", (object?)"SQL Managed Instance General Purpose - Compute Gen5"),
                ("sku_name", "1 vCore"),
                ("meter_name", "vCore"),
                ("meter_id", "meter-sqlmi-payg"),
                ("price_type", "Consumption"),
                ("retail_price", 0.15),
                ("unit_of_measure", "1 Hour"),
            },
            new[]
            {
                ("product_name", (object?)"SQL Managed Instance General Purpose - Storage"),
                ("sku_name", "General Purpose"),
                ("meter_name", "General Purpose Data Stored"),
                ("price_type", "Consumption"),
                ("retail_price", 0.115),
                ("unit_of_measure", "1 GB/Month"),
            },
            new[]
            {
                ("product_name", (object?)"SQL Managed Instance General Purpose - Compute Gen5"),
                ("sku_name", "vCore"),
                ("meter_name", "vCore"),
                ("price_type", "Reservation"),
                ("reservation_term", "1 Year"),
                ("retail_price", 867.0),
            });

        var cache = new FakePriceCache { QueryCachedFn = (_, _, _, _) => cached };
        var repo = BuildRepo(cache, new FakeRetailPriceClient());

        var selected = repo.GetSqlManagedInstancePrices("eastus", "GeneralPurpose", "Gen5", 1);

        Assert.Equal(0.15, selected.PaygHourlyPerVcore);
        Assert.Equal(0.115, selected.StorageMonthlyPerGb);
        Assert.Equal(867.0, selected.Ri1yTotalPerVcore);
        Assert.Equal("meter-sqlmi-payg", selected.PaygMeterId);
    }

    // Python: test_sql_managed_instance_selection_uses_exact_vcore_sku_and_non_zr_storage
    [Fact]
    public void Selection_UsesExactVcoreSkuAndNonZrStorage()
    {
        var cached = PriceRowFactory.Many(
            new[]
            {
                ("product_name", (object?)"SQL Managed Instance Business Critical - Compute Gen5"),
                ("sku_name", "40 vCore"),
                ("meter_name", "vCore"),
                ("price_type", "Consumption"),
                ("retail_price", 12.1774),
                ("unit_of_measure", "1 Hour"),
            },
            new[]
            {
                ("product_name", (object?)"SQL Managed Instance Business Critical - Compute Gen5"),
                ("sku_name", "24 vCore"),
                ("meter_name", "vCore"),
                ("price_type", "Consumption"),
                ("retail_price", 7.30644),
                ("unit_of_measure", "1 Hour"),
            },
            new[]
            {
                ("product_name", (object?)"SQL Managed Instance Business Critical - Storage"),
                ("sku_name", "Business Critical Zone Redundancy"),
                ("meter_name", "Business Critical Zone Redundancy Data Stored"),
                ("price_type", "Consumption"),
                ("retail_price", 0.50),
                ("unit_of_measure", "1 GB/Month"),
            },
            new[]
            {
                ("product_name", (object?)"SQL Managed Instance Business Critical - Storage"),
                ("sku_name", "Business Critical"),
                ("meter_name", "Business Critical Data Stored"),
                ("price_type", "Consumption"),
                ("retail_price", 0.25),
                ("unit_of_measure", "1 GB/Month"),
            });

        var cache = new FakePriceCache { QueryCachedFn = (_, _, _, _) => cached };
        var repo = BuildRepo(cache, new FakeRetailPriceClient());

        var selected = repo.GetSqlManagedInstancePrices("eastus", "BusinessCritical", "Gen5", 24, false);

        // Python: assertAlmostEqual(payg, 7.30644 / 24)
        Assert.NotNull(selected.PaygHourlyPerVcore);
        Assert.Equal(7.30644 / 24, selected.PaygHourlyPerVcore!.Value, 10);
        Assert.Equal("24 vCore", selected.ComputeSkuName);
        Assert.Equal(0.25, selected.StorageMonthlyPerGb);
        Assert.Equal("Business Critical", selected.StorageSkuName);
    }

    // Python: test_sql_managed_instance_selection_uses_zone_redundant_prices_when_enabled
    [Fact]
    public void Selection_UsesZoneRedundantPricesWhenEnabled()
    {
        var cached = PriceRowFactory.Many(
            new[]
            {
                ("product_name", (object?)"SQL Managed Instance Business Critical - Compute Gen5"),
                ("sku_name", "24 vCore"),
                ("meter_name", "vCore"),
                ("price_type", "Consumption"),
                ("retail_price", 7.30644),
                ("unit_of_measure", "1 Hour"),
            },
            new[]
            {
                ("product_name", (object?)"SQL Managed Instance Business Critical - Compute Gen5"),
                ("sku_name", "vCore ZR Zone Redundancy"),
                ("meter_name", "vCore"),
                ("price_type", "Consumption"),
                ("retail_price", 0.426209),
                ("unit_of_measure", "1 Hour"),
            },
            new[]
            {
                ("product_name", (object?)"SQL Managed Instance Business Critical - Storage"),
                ("sku_name", "Business Critical"),
                ("meter_name", "Business Critical Data Stored"),
                ("price_type", "Consumption"),
                ("retail_price", 0.25),
                ("unit_of_measure", "1 GB/Month"),
            },
            new[]
            {
                ("product_name", (object?)"SQL Managed Instance Business Critical - Storage"),
                ("sku_name", "Business Critical Zone Redundancy"),
                ("meter_name", "Business Critical Zone Redundancy Data Stored"),
                ("price_type", "Consumption"),
                ("retail_price", 0.50),
                ("unit_of_measure", "1 GB/Month"),
            });

        var cache = new FakePriceCache { QueryCachedFn = (_, _, _, _) => cached };
        var repo = BuildRepo(cache, new FakeRetailPriceClient());

        var selected = repo.GetSqlManagedInstancePrices("eastus", "BusinessCritical", "Gen5", 24, true);

        Assert.Equal(0.426209, selected.PaygHourlyPerVcore);
        Assert.Equal("vCore ZR Zone Redundancy", selected.ComputeSkuName);
        Assert.Equal(0.50, selected.StorageMonthlyPerGb);
        Assert.Equal("Business Critical Zone Redundancy", selected.StorageSkuName);
    }
}
