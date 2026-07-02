using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Tests de SELECCIÓN del servicio "redis" de
/// optimizacion-costos-api-github/tests/test_paas_pricing.py:
///   - test_redis_selection_uses_capacity_token         → port fiel
///   - test_redis_classic_refresh_is_scoped_to_sku_name → port fiel
///
/// Los montos esperados son EXACTAMENTE los del Python.
///
/// NO portado: test_redis_selection_uses_ai_assistant_only_from_real_candidates. Ese test
/// del Python parchea el asistente de IA (_assistant_select) y verifica la selección VÍA IA.
/// El fallback de IA está DIFERIDO a una fase posterior (queda fuera del alcance de Fase 1),
/// así que no existe un port fiel. En su lugar, el test
/// Redis_Selection_Without_Assistant_Returns_Null_When_No_Deterministic_Candidate documenta
/// HONESTAMENTE la conducta de Fase 1 (sin IA) y NO pretende reproducir el comportamiento de IA.
/// </summary>
public sealed class PriceSelection_RedisTests
{
    private static SqlPriceRepository BuildRepo(FakePriceCache cache, FakeRetailPriceClient client)
        => new SqlPriceRepository(cache, client, new FakePricingConstants());

    /// <summary>
    /// Python: test_redis_selection_uses_capacity_token. La selección de C1 (capacidad 1)
    /// elige el PAYG horario 0.10 (no el de C0 0.05) y la reserva 1y 500.0.
    /// </summary>
    [Fact]
    public void Redis_Selection_Uses_Capacity_Token()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Redis Cache Standard C0"),
                ("sku_name", "Standard"),
                ("meter_name", "Standard C0"),
                ("price_type", "Consumption"),
                ("retail_price", 0.05),
                ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Redis Cache Standard C1"),
                ("sku_name", "Standard"),
                ("meter_name", "Standard C1"),
                ("meter_id", "meter-redis-payg"),
                ("price_type", "Consumption"),
                ("retail_price", 0.10),
                ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Redis Cache Standard C1"),
                ("sku_name", "Standard"),
                ("meter_name", "Standard C1"),
                ("price_type", "Reservation"),
                ("reservation_term", "1 Year"),
                ("retail_price", 500.0),
            });

        var selected = SqlPriceRepository.SelectRedisPrices(cached, "Standard", 1);

        Assert.Equal(0.10, selected.PaygHourly);
        Assert.Equal(500.0, selected.Ri1yTotal);
        Assert.Equal("meter-redis-payg", selected.PaygMeterId);
    }

    /// <summary>
    /// Test HONESTO de Fase 1 (NO es un port del test de IA del Python). El fallback de IA
    /// (_assistant_select) que el Python ejercita en
    /// test_redis_selection_uses_ai_assistant_only_from_real_candidates está DIFERIDO a una
    /// fase posterior y queda fuera del alcance de Fase 1.
    ///
    /// Este test documenta el contrato real de Fase 1: SIN IA, ante un candidato (Premium P2,
    /// 1.25) que la IA habría podido elegir para una capacidad pedida (Enterprise 9) que NO
    /// aparece en los candidatos deterministas, la selección determinista NO inventa precio:
    /// payg_hourly queda en null y match_strategy en null. NO afirma reproducir la conducta de IA.
    /// </summary>
    [Fact]
    public void Redis_Selection_Phase1_NoAiFallback_ReturnsNull_WhenOnlyAiWouldHaveMatched()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("price_id", 55L),
                ("product_name", "Redis Cache Premium P2"),
                ("sku_name", "Premium"),
                ("meter_name", "Premium P2"),
                ("price_type", "Consumption"),
                ("retail_price", 1.25),
                ("unit_of_measure", "1 Hour"),
            });

        var selected = SqlPriceRepository.SelectRedisPrices(cached, "Enterprise", 9);

        Assert.Null(selected.PaygHourly);
        Assert.Null(selected.MatchStrategy);
    }

    /// <summary>
    /// Python: test_redis_classic_refresh_is_scoped_to_sku_name. Con el fetch_query rancio,
    /// get_redis_prices refresca vía fetch_redis_prices("Standard","eastus"),
    /// delete_by_fetch_query/insert con "redis Standard eastus" y consulta la cache por el
    /// servicio "Redis Cache" en "eastus". El payg resultante es 0.10.
    /// </summary>
    [Fact]
    public void Redis_Classic_Refresh_Is_Scoped_To_Sku_Name()
    {
        var item = PriceRowFactory.Of(
            ("product_name", "Redis Cache Standard C1"),
            ("sku_name", "Standard"),
            ("meter_name", "Standard C1"),
            ("price_type", "Consumption"),
            ("retail_price", 0.10));

        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => false,
            QueryCachedFn = (_, _, _, _) => new[] { item },
        };
        var client = new FakeRetailPriceClient
        {
            FetchRedisPricesFn = (_, _) => new[] { item },
        };
        var repo = BuildRepo(cache, client);

        var selected = repo.GetRedisPrices("Standard", 1, "eastus");

        Assert.Equal(0.10, selected.PaygHourly);

        // is_fresh.assert_called_once_with("redis Standard eastus")
        Assert.Equal("redis Standard eastus", Assert.Single(cache.IsFetchQueryFreshCalls));

        // fetch_redis.assert_called_once_with("Standard", "eastus")
        Assert.Equal(("Standard", "eastus"), Assert.Single(client.FetchRedisPricesCalls));

        // delete_stale.assert_called_once_with("redis Standard eastus")  (delete por fetch_query)
        Assert.Equal("redis Standard eastus", Assert.Single(cache.DeleteByFetchQueryCalls));

        // insert_batch.assert_called_once_with([item], "redis Standard eastus")
        var insert = Assert.Single(cache.InsertCalls);
        Assert.Equal("redis Standard eastus", insert.FetchQuery);
        Assert.Equal(new[] { item }, insert.Rows);

        // query_cached.assert_called_once_with("Redis Cache", "eastus")
        var query = Assert.Single(cache.QueryCachedCalls);
        Assert.Equal("Redis Cache", query.ServiceName);
        Assert.Equal("eastus", query.Region);
        Assert.Null(query.ArmSkuName);
        Assert.Null(query.SkuName);
    }
}
