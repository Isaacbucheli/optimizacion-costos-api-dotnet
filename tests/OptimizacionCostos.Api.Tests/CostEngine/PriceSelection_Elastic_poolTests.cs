using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Port de tests/test_paas_pricing.py — tests de SELECCIÓN del servicio "elastic_pool"
/// (los que llaman <c>prices._select_elastic_pool_price</c>). Arrays de precios y montos
/// esperados copiados EXACTAMENTE del Python; no se recalcula nada.
///
/// Tests portados:
///   - test_elastic_pool_vcore_selects_exact_vcore_meter
///   - test_elastic_pool_dtu_selects_pack_and_normalizes_per_day
///   - test_elastic_pool_no_exact_capacity_match_returns_none
/// Más un test de orquestación caché/refresh sobre GetElasticPoolPrices con los fakes.
/// </summary>
public sealed class PriceSelection_Elastic_poolTests
{
    private static SqlPriceRepository MakeRepo(FakePriceCache cache, FakeRetailPriceClient client)
        => new(cache, client, new FakePricingConstants());

    // Python: test_elastic_pool_vcore_selects_exact_vcore_meter
    [Fact]
    public void Vcore_SelectsExactVcoreMeter()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "SQL Database Single/Elastic Pool General Purpose - Compute Gen5"),
                ("sku_name", "8 vCore"),
                ("meter_name", "8 vCore"),
                ("price_type", "Consumption"),
                ("retail_price", 1.40),
                ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "SQL Database Single/Elastic Pool General Purpose - Compute Gen5"),
                ("sku_name", "4 vCore"),
                ("meter_name", "4 vCore"),
                ("price_type", "Consumption"),
                ("retail_price", 0.70),
                ("unit_of_measure", "1 Hour"),
            });

        var selected = SqlPriceRepository.SelectElasticPoolPrice(cached, "GeneralPurpose", "Gen5", 8);

        Assert.NotNull(selected);
        Assert.Equal(1.40, selected!.PaygHourly);
        Assert.Equal("vCore", selected.Model);
    }

    // Python: test_elastic_pool_dtu_selects_pack_and_normalizes_per_day
    [Fact]
    public void Dtu_SelectsPackAndNormalizesPerDay()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "SQL Database Elastic Pool - Standard"),
                ("sku_name", "100 DTU Pack"),
                ("meter_name", "eDTUs"),
                ("price_type", "Consumption"),
                ("retail_price", 4.80),
                ("unit_of_measure", "1/Day"),
            });

        var selected = SqlPriceRepository.SelectElasticPoolPrice(cached, "Standard", "", 100);

        Assert.NotNull(selected);
        Assert.Equal("DTU", selected!.Model);
        Assert.Equal(4.80 / 24.0, selected.PaygHourly!.Value, 10);
    }

    // Python: test_elastic_pool_no_exact_capacity_match_returns_none
    [Fact]
    public void NoExactCapacityMatch_ReturnsNull()
    {
        // Blindaje: capacidad sin meter exacto → null (no aproxima).
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "SQL Database Single/Elastic Pool General Purpose - Compute Gen5"),
                ("sku_name", "8 vCore"),
                ("meter_name", "8 vCore"),
                ("price_type", "Consumption"),
                ("retail_price", 1.40),
                ("unit_of_measure", "1 Hour"),
            });

        Assert.Null(SqlPriceRepository.SelectElasticPoolPrice(cached, "GeneralPurpose", "Gen5", 6));
    }

    // Orquestación: fetch_query rancio → FetchElasticPoolPrices + DeleteByFetchQuery + Insert,
    // luego selecciona de lo cacheado. (Cubre get_elastic_pool_prices.)
    [Fact]
    public void GetElasticPoolPrices_StaleFetchQuery_RefreshesThenSelects()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "SQL Database Single/Elastic Pool General Purpose - Compute Gen5"),
                ("sku_name", "8 vCore"),
                ("meter_name", "8 vCore"),
                ("price_type", "Consumption"),
                ("retail_price", 1.40),
                ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            });

        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => false,
            QueryCachedFn = (_, _, _, _) => cached,
        };
        var client = new FakeRetailPriceClient
        {
            FetchElasticPoolPricesFn = _ => cached,
        };
        var repo = MakeRepo(cache, client);

        var selected = repo.GetElasticPoolPrices("GeneralPurpose", "Gen5", 8, "East US 2");

        Assert.NotNull(selected);
        Assert.Equal(1.40, selected!.PaygHourly);
        Assert.Equal("vCore", selected.Model);
        // refresh ejecutado: fetch por region + borrado por fetch_query + insert.
        Assert.Equal(new[] { "East US 2" }, client.FetchElasticPoolPricesCalls);
        Assert.Equal(new[] { "elastic_pool East US 2" }, cache.DeleteByFetchQueryCalls);
        Assert.Single(cache.InsertCalls);
    }

    // Orquestación: fetch_query fresco → no se llama al cliente; solo selecciona de la caché.
    [Fact]
    public void GetElasticPoolPrices_FreshFetchQuery_DoesNotFetch()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "SQL Database Elastic Pool - Standard"),
                ("sku_name", "100 DTU Pack"),
                ("meter_name", "eDTUs"),
                ("price_type", "Consumption"),
                ("retail_price", 4.80),
                ("unit_of_measure", "1/Day"),
            });

        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => true,
            QueryCachedFn = (_, _, _, _) => cached,
        };
        var client = new FakeRetailPriceClient();
        var repo = MakeRepo(cache, client);

        var selected = repo.GetElasticPoolPrices("Standard", "", 100, "East US 2");

        Assert.NotNull(selected);
        Assert.Equal("DTU", selected!.Model);
        Assert.Equal(4.80 / 24.0, selected.PaygHourly!.Value, 10);
        Assert.Empty(client.FetchElasticPoolPricesCalls);
        Assert.Empty(cache.DeleteByFetchQueryCalls);
        Assert.Empty(cache.InsertCalls);
    }
}
