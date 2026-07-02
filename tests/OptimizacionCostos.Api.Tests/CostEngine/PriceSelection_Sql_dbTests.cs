using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Port 1:1 de los tests de SELECCIÓN de precios sql_db de tests/test_paas_pricing.py:
///   - test_sql_hyperscale_serverless_selects_paid_vcore_meter  → 0.378
///   - test_sql_gp_gen5_provisioned_selects_non_serverless_vcore_meter → 0.2609
///
/// Los tests Python ejercitan el selector directo (prices._select_sql_db_price). Aquí se
/// ejercita el equivalente <see cref="SqlPriceRepository.SelectSqlDbPriceItem"/> y, además,
/// el método público <see cref="SqlPriceRepository.GetSqlDbPriceDetails"/> con caché fresca
/// (IsFetchQueryFresh=true → no se dispara fetch), verificando el ruteo de caché.
///
/// MONTOS EXACTOS copiados del Python; NO se recalculan.
/// </summary>
public sealed class PriceSelection_Sql_dbTests
{
    private static SqlPriceRepository MakeRepo(FakePriceCache cache, FakeRetailPriceClient client)
        => new(cache, client, new FakePricingConstants());

    // ---------- Hyperscale Serverless: elige el meter vCore pagado (0.378) ----------

    private static IReadOnlyList<PriceRow> HyperscaleServerlessCached() => PriceRowFactory.Many(
        new (string, object?)[]
        {
            ("product_name", "SQL Database Hyperscale - Serverless - Compute Gen5"),
            ("sku_name", "1 vCore"),
            ("meter_name", "1 vCore"),
            ("price_type", "Consumption"),
            ("retail_price", 0.378),
            ("unit_of_measure", "1 Hour"),
        },
        new (string, object?)[]
        {
            ("product_name", "SQL Database General Purpose - Serverless - Compute Gen5"),
            ("sku_name", "2 vCore"),
            ("meter_name", "2 vCore - Free"),
            ("price_type", "Consumption"),
            ("retail_price", 0.0),
            ("unit_of_measure", "1 Hour"),
        },
        new (string, object?)[]
        {
            ("product_name", "SQL Database SingleDB Hyperscale - Storage"),
            ("sku_name", "Backup - RA-GRS"),
            ("meter_name", "Backup - RA-GRS Data Stored"),
            ("price_type", "Consumption"),
            ("retail_price", 0.2),
            ("unit_of_measure", "1 GB/Month"),
        });

    [Fact]
    public void Selector_HyperscaleServerless_SelectsPaidVcoreMeter()
    {
        var item = SqlPriceRepository.SelectSqlDbPriceItem(
            HyperscaleServerlessCached(), "HS_S_Gen5", skuTier: "Hyperscale", computeTier: "Serverless");

        Assert.NotNull(item);
        Assert.Equal(0.378, item!.RetailPrice);
    }

    [Fact]
    public void GetSqlDbPriceDetails_HyperscaleServerless_SelectsPaidVcoreMeter()
    {
        var cached = HyperscaleServerlessCached();
        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => true, // cache fresca: no fetch
            QueryCachedFn = (_, _, _, _) => cached,
        };
        var client = new FakeRetailPriceClient();
        var repo = MakeRepo(cache, client);

        var details = repo.GetSqlDbPriceDetails(
            "HS_S_Gen5", "eastus", skuTier: "Hyperscale", computeTier: "Serverless");

        Assert.NotNull(details);
        Assert.Equal(0.378, details!.RetailPrice);

        // mapped_lookup => frescura por fetch_query y _query_cached por región (sin sku_name).
        Assert.Equal("sql_db HS_S_Gen5 eastus", Assert.Single(cache.IsFetchQueryFreshCalls));
        Assert.Empty(client.FetchSqlDbPricesCalls); // fresca => no fetch
        var qc = Assert.Single(cache.QueryCachedCalls);
        Assert.Equal(("SQL Database", "eastus", (string?)null, (string?)null), qc);
    }

    // ---------- GP_Gen5 Provisioned: elige el meter vCore NO serverless (0.2609) ----------

    private static IReadOnlyList<PriceRow> GpGen5ProvisionedCached() => PriceRowFactory.Many(
        new (string, object?)[]
        {
            ("product_name", "SQL Database Single/Elastic Pool General Purpose - Serverless - Compute Gen5"),
            ("sku_name", "1 vCore"),
            ("meter_name", "1 vCore"),
            ("price_type", "Consumption"),
            ("retail_price", 0.5218),
            ("unit_of_measure", "1 Hour"),
        },
        new (string, object?)[]
        {
            ("product_name", "SQL Database Single/Elastic Pool General Purpose - Compute Gen5"),
            ("sku_name", "1 vCore"),
            ("meter_name", "1 vCore"),
            ("meter_id", "meter-sqldb-payg"),
            ("price_type", "Consumption"),
            ("retail_price", 0.2609),
            ("unit_of_measure", "1 Hour"),
            ("is_primary_meter", true),
        });

    [Fact]
    public void Selector_GpGen5Provisioned_SelectsNonServerlessVcoreMeter()
    {
        var item = SqlPriceRepository.SelectSqlDbPriceItem(
            GpGen5ProvisionedCached(), "GP_Gen5", skuTier: "GeneralPurpose", computeTier: "Provisioned");

        Assert.NotNull(item);
        Assert.Equal(0.2609, item!.RetailPrice);
    }

    [Fact]
    public void GetSqlDbPriceDetails_GpGen5Provisioned_SelectsNonServerlessVcoreMeter()
    {
        var cached = GpGen5ProvisionedCached();
        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => true,
            QueryCachedFn = (_, _, _, _) => cached,
        };
        var client = new FakeRetailPriceClient();
        var repo = MakeRepo(cache, client);

        var details = repo.GetSqlDbPriceDetails(
            "GP_Gen5", "eastus", skuTier: "GeneralPurpose", computeTier: "Provisioned");

        Assert.NotNull(details);
        Assert.Equal(0.2609, details!.RetailPrice);
        Assert.Equal("1 vCore", details.MeterName);
        Assert.Equal("SQL Database Single/Elastic Pool General Purpose - Compute Gen5", details.ProductName);
        Assert.Equal("meter-sqldb-payg", details.PaygMeterId);

        // mapped_lookup (sku empieza con "gp_gen5"): frescura por fetch_query, query por región.
        Assert.Equal("sql_db GP_Gen5 eastus", Assert.Single(cache.IsFetchQueryFreshCalls));
        var qc = Assert.Single(cache.QueryCachedCalls);
        Assert.Equal(("SQL Database", "eastus", (string?)null, (string?)null), qc);
    }

    // ---------- Refresh: mapped_lookup rancio dispara fetch(region, null) ----------

    [Fact]
    public void GetSqlDbPriceDetails_MappedLookup_Stale_FetchesByRegionAndRefreshesByFetchQuery()
    {
        var refreshed = GpGen5ProvisionedCached();
        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => false, // rancio => dispara fetch
            QueryCachedFn = (_, _, _, _) => refreshed,
        };
        var client = new FakeRetailPriceClient
        {
            FetchSqlDbPricesFn = (_, _) => refreshed,
        };
        var repo = MakeRepo(cache, client);

        var details = repo.GetSqlDbPriceDetails(
            "GP_Gen5", "eastus", skuTier: "GeneralPurpose", computeTier: "Provisioned");

        Assert.NotNull(details);
        Assert.Equal(0.2609, details!.RetailPrice);

        // mapped_lookup => fetch_sql_db_prices(region, None) y refresh por fetch_query.
        var fetch = Assert.Single(client.FetchSqlDbPricesCalls);
        Assert.Equal(("eastus", (string?)null), fetch);
        Assert.Equal("sql_db GP_Gen5 eastus", Assert.Single(cache.DeleteByFetchQueryCalls));
        var insert = Assert.Single(cache.InsertCalls);
        Assert.Equal("sql_db GP_Gen5 eastus", insert.FetchQuery);
        Assert.Empty(cache.DeleteStaleCalls); // mapped no usa DeleteStale
    }

    // ---------- Refresh: lookup NO mapeado rancio dispara fetch(region, sku) + DeleteStale ----------

    [Fact]
    public void GetSqlDbPriceDetails_NonMapped_Stale_FetchesBySkuAndRefreshesByScope()
    {
        // sku no mapeado (no basic/gp_*/serverless/generalpurpose): "S3".
        var standardRow = PriceRowFactory.Of(
            ("product_name", "SQL Database Single Standard"),
            ("sku_name", "S3"),
            ("meter_name", "100 DTU"),
            ("price_type", "Consumption"),
            ("retail_price", 0.20),
            ("unit_of_measure", "1 Hour"),
            ("is_primary_meter", true));
        var rows = new[] { standardRow };

        var cache = new FakePriceCache
        {
            IsCacheFreshForFn = (_, _, _, _) => false, // rancio => fetch
            QueryCachedFn = (_, _, _, _) => rows,
        };
        var client = new FakeRetailPriceClient
        {
            FetchSqlDbPricesFn = (_, _) => rows,
        };
        var repo = MakeRepo(cache, client);

        var details = repo.GetSqlDbPriceDetails("S3", "eastus");

        Assert.NotNull(details);
        Assert.Equal(0.20, details!.RetailPrice);

        // No mapeado => frescura por servicio+region+sku, fetch(region, sku), DeleteStale por sku, query con sku.
        var fresh = Assert.Single(cache.IsCacheFreshForCalls);
        Assert.Equal(("SQL Database", "eastus", (string?)null, "S3"), fresh);
        var fetch = Assert.Single(client.FetchSqlDbPricesCalls);
        Assert.Equal(("eastus", "S3"), fetch);
        var del = Assert.Single(cache.DeleteStaleCalls);
        Assert.Equal(("SQL Database", "eastus", (string?)null, "S3"), del);
        Assert.Empty(cache.DeleteByFetchQueryCalls);
        var qc = Assert.Single(cache.QueryCachedCalls);
        Assert.Equal(("SQL Database", "eastus", (string?)null, "S3"), qc);
    }

    // ---------- Sin item determinista => null ----------

    [Fact]
    public void GetSqlDbPriceDetails_NoMatch_ReturnsNull()
    {
        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => true,
            QueryCachedFn = (_, _, _, _) => Array.Empty<PriceRow>(),
        };
        var repo = MakeRepo(cache, new FakeRetailPriceClient());

        var details = repo.GetSqlDbPriceDetails(
            "GP_S_Gen5", "eastus", skuTier: "GeneralPurpose", computeTier: "Serverless");

        Assert.Null(details);
    }
}
