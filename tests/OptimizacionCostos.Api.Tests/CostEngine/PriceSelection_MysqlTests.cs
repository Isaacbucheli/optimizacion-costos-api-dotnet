using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Tests de paridad de la SELECCIÓN de precios del servicio "mysql"
/// (GetMySqlFlexPrices / GetMySqlStoragePricePerGb) contra
/// app/pricing/price_repository.py y tests/test_paas_pricing.py.
///
/// Los montos/decisiones esperados son copiados EXACTAMENTE de los tests Python.
/// Único test de caché/selección de mysql en el Python:
///   test_mysql_storage_refresh_uses_storage_fetch_query_not_region_cache (línea 372).
/// Los demás tests aquí ejercitan el selector determinista de flex/storage replicando
/// la lógica 1:1 del Python (no recalculan montos: usan los precios literales de los arrays).
/// </summary>
public sealed class PriceSelection_MysqlTests
{
    private static SqlPriceRepository NewRepo(FakePriceCache cache, FakeRetailPriceClient client)
        => new(cache, client, new FakePricingConstants());

    // ---------------------------------------------------------------------------------
    // Port directo de test_mysql_storage_refresh_uses_storage_fetch_query_not_region_cache
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Port de test_mysql_storage_refresh_uses_storage_fetch_query_not_region_cache.
    /// fetch_query no fresco → fetch_mysql_storage_price("eastus"), borra+inserta por
    /// fetch_query "mysql_storage eastus", y el precio elegido es 0.115.
    /// </summary>
    [Fact]
    public void MysqlStorage_Refresh_UsesStorageFetchQuery_NotRegionCache()
    {
        var storageItem = PriceRowFactory.Of(
            ("product_name", "Azure Database for MySQL Flexible Server"),
            ("meter_name", "Storage"),
            ("price_type", "Consumption"),
            ("retail_price", 0.115),
            ("unit_of_measure", "1 GB/Month"),
            ("is_primary_meter", true));

        var canned = new[] { storageItem };

        var client = new FakeRetailPriceClient
        {
            FetchMySqlStoragePriceFn = _ => canned,
        };
        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => false,            // _is_fetch_query_fresh -> False
            QueryCachedFn = (_, _, _, _) => canned,      // _query_cached -> [storage_item]
        };

        var repo = NewRepo(cache, client);

        var price = repo.GetMySqlStoragePricePerGb("eastus");

        Assert.Equal(0.115, price);
        // fetch_storage.assert_called_once_with("eastus")
        Assert.Equal("eastus", Assert.Single(client.FetchMySqlStoragePriceCalls));
        // delete_by_query.assert_called_once_with("mysql_storage eastus")
        Assert.Equal("mysql_storage eastus", Assert.Single(cache.DeleteByFetchQueryCalls));
        // insert_batch.assert_called_once_with([storage_item], "mysql_storage eastus")
        var insert = Assert.Single(cache.InsertCalls);
        Assert.Equal("mysql_storage eastus", insert.FetchQuery);
        Assert.Equal(canned, insert.Rows);
    }

    // ---------------------------------------------------------------------------------
    // Selección determinista de storage (_select_mysql_storage_price)
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Cache fresca: no se hace fetch; el selector elige el meter "Storage" de Flexible Server.
    /// Precio literal del array: 0.115.
    /// </summary>
    [Fact]
    public void MysqlStorage_SelectsFlexibleServerStorageMeter()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Azure Database for MySQL Flexible Server"),
                ("meter_name", "vCore"),
                ("price_type", "Consumption"),
                ("retail_price", 0.04),
                ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure Database for MySQL Flexible Server"),
                ("meter_name", "Storage"),
                ("price_type", "Consumption"),
                ("retail_price", 0.115),
                ("unit_of_measure", "1 GB/Month"),
                ("is_primary_meter", true),
            });

        var client = new FakeRetailPriceClient();
        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => true,             // fresca → no fetch
            QueryCachedFn = (_, _, _, _) => cached,
        };

        var price = NewRepo(cache, client).GetMySqlStoragePricePerGb("eastus");

        Assert.Equal(0.115, price);
        Assert.Empty(client.FetchMySqlStoragePriceCalls);
        Assert.Empty(cache.DeleteByFetchQueryCalls);
        Assert.Empty(cache.InsertCalls);
    }

    /// <summary>
    /// Sin meter "Storage" de Flexible Server: el determinista no halla y (sin IA) devuelve null.
    /// </summary>
    [Fact]
    public void MysqlStorage_ReturnsNull_WhenNoStorageMeter()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Azure Database for MySQL Flexible Server"),
                ("meter_name", "vCore"),
                ("price_type", "Consumption"),
                ("retail_price", 0.04),
                ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            });

        var cache = new FakePriceCache { QueryCachedFn = (_, _, _, _) => cached };

        var price = NewRepo(cache, new FakeRetailPriceClient()).GetMySqlStoragePricePerGb("eastus");

        Assert.Null(price);
    }

    // ---------------------------------------------------------------------------------
    // Selección determinista de flex compute (_select_mysql_flex_prices)
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Burstable (Standard_B1ms / tier "burstable"): selecciona el compute Consumption del
    /// producto Burstable (meter empieza con "B"), excluye Storage. is_per_vcore=false
    /// (meter sin "vcore"). RI nula (Burstable no tiene RI en este array). Precio: 0.04.
    /// Replica el escenario de test_mysql_calculator_adds_storage_and_skips_burstable_ri
    /// a nivel de SELECCIÓN (no del calculador).
    /// </summary>
    [Fact]
    public void MysqlFlex_Burstable_SelectsComputeAndSkipsRi()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Azure Database for MySQL Flexible Server Burstable"),
                ("sku_name", "B1ms"),
                ("meter_name", "B1ms Compute"),
                ("price_type", "Consumption"),
                ("retail_price", 0.04),
                ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure Database for MySQL Flexible Server Burstable"),
                ("meter_name", "Storage"),
                ("price_type", "Consumption"),
                ("retail_price", 0.10),
                ("unit_of_measure", "1 GB/Month"),
                ("is_primary_meter", true),
            });

        var cache = new FakePriceCache { QueryCachedFn = (_, _, _, _) => cached };

        var result = NewRepo(cache, new FakeRetailPriceClient())
            .GetMySqlFlexPrices("Standard_B1ms", "eastus", "burstable");

        Assert.Equal(0.04, result.PaygHourly);
        Assert.Null(result.Ri1yTotal);
        Assert.Null(result.Ri3yTotal);
        Assert.False(result.IsPerVcore);
        // Paridad Python: match_strategy = compute.get("ai_match_strategy") if compute else "deterministic".
        // En ruta determinista las filas no tienen ai_match_strategy → con compute encontrado es None (null).
        Assert.Null(result.MatchStrategy);
        Assert.Null(result.MatchConfidence);
    }

    /// <summary>
    /// General Purpose por vCore: meter "vCore", is_per_vcore=true, y captura RI 1y/3y del array.
    /// Precios literales: compute 0.121, RI1y 950.0, RI3y 2400.0.
    /// </summary>
    [Fact]
    public void MysqlFlex_GeneralPurpose_PerVcore_WithReservations()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Azure Database for MySQL Flexible Server General Purpose Ddsv4"),
                ("sku_name", "D2ds_v4"),
                ("meter_name", "vCore"),
                ("meter_id", "meter-mysql-payg"),
                ("price_type", "Consumption"),
                ("retail_price", 0.121),
                ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure Database for MySQL Flexible Server General Purpose Ddsv4"),
                ("meter_name", "vCore Reserved"),
                ("price_type", "Reservation"),
                ("reservation_term", "1 Year"),
                ("retail_price", 950.0),
                ("unit_of_measure", "1 Hour"),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure Database for MySQL Flexible Server General Purpose Ddsv4"),
                ("meter_name", "vCore Reserved"),
                ("price_type", "Reservation"),
                ("reservation_term", "3 Years"),
                ("retail_price", 2400.0),
                ("unit_of_measure", "1 Hour"),
            });

        var cache = new FakePriceCache { QueryCachedFn = (_, _, _, _) => cached };

        var result = NewRepo(cache, new FakeRetailPriceClient())
            .GetMySqlFlexPrices("Standard_D2ds_v4", "eastus", "GeneralPurpose");

        Assert.Equal(0.121, result.PaygHourly);
        Assert.Equal(950.0, result.Ri1yTotal);
        Assert.Equal(2400.0, result.Ri3yTotal);
        Assert.True(result.IsPerVcore);
        // Paridad Python: con compute encontrado, match_strategy = ai_match_strategy = None (null).
        Assert.Null(result.MatchStrategy);
        Assert.Equal("meter-mysql-payg", result.PaygMeterId);
    }

    /// <summary>
    /// Filtro por serie: hay filas de Burstable y de General Purpose. Pidiendo Burstable,
    /// el filtro de tokens deja solo la serie Burstable y elige su compute (0.04),
    /// NO el de General Purpose (0.121).
    /// </summary>
    [Fact]
    public void MysqlFlex_SeriesFilter_PicksMatchingSeriesOnly()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Azure Database for MySQL Flexible Server Burstable"),
                ("sku_name", "B1ms"),
                ("meter_name", "B1ms Compute"),
                ("price_type", "Consumption"),
                ("retail_price", 0.04),
                ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure Database for MySQL Flexible Server General Purpose Ddsv4"),
                ("sku_name", "D2ds_v4"),
                ("meter_name", "vCore"),
                ("price_type", "Consumption"),
                ("retail_price", 0.121),
                ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            });

        var cache = new FakePriceCache { QueryCachedFn = (_, _, _, _) => cached };

        var result = NewRepo(cache, new FakeRetailPriceClient())
            .GetMySqlFlexPrices("Standard_B1ms", "eastus", "burstable");

        Assert.Equal(0.04, result.PaygHourly);
        Assert.False(result.IsPerVcore);
    }

    /// <summary>
    /// Sin compute Consumption válido: el determinista no halla compute. Paridad Python:
    /// match_strategy = compute.get("ai_match_strategy") if compute else "deterministic" →
    /// SIN compute, el valor es "deterministic". payg_hourly y RI quedan null.
    /// (El fallback de IA _assistant_select que Python intentaría aquí está DIFERIDO a una
    /// fase posterior; sin IA, payg_hourly se queda en null.)
    /// </summary>
    [Fact]
    public void MysqlFlex_ReturnsNullCompute_WhenNoConsumptionMeter()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Azure Database for MySQL Flexible Server Burstable"),
                ("meter_name", "Storage"),
                ("price_type", "Consumption"),
                ("retail_price", 0.10),
                ("unit_of_measure", "1 GB/Month"),
                ("is_primary_meter", true),
            });

        var cache = new FakePriceCache { QueryCachedFn = (_, _, _, _) => cached };

        var result = NewRepo(cache, new FakeRetailPriceClient())
            .GetMySqlFlexPrices("Standard_B1ms", "eastus", "burstable");

        Assert.Null(result.PaygHourly);
        Assert.Null(result.Ri1yTotal);
        Assert.Null(result.Ri3yTotal);
        Assert.False(result.IsPerVcore);
        // Paridad Python: sin compute → match_strategy = "deterministic".
        Assert.Equal("deterministic", result.MatchStrategy);
    }

    /// <summary>
    /// flex: fetch_query no fresco → fetch_mysql_flex_prices(arm_sku, region), borra+inserta
    /// por fetch_query "mysql Standard_B1ms eastus" y consulta cache por servicio+región.
    /// </summary>
    [Fact]
    public void MysqlFlex_Refresh_UsesFlexFetchQuery()
    {
        var computeItem = PriceRowFactory.Of(
            ("product_name", "Azure Database for MySQL Flexible Server Burstable"),
            ("meter_name", "B1ms Compute"),
            ("price_type", "Consumption"),
            ("retail_price", 0.04),
            ("unit_of_measure", "1 Hour"),
            ("is_primary_meter", true));
        var canned = new[] { computeItem };

        var client = new FakeRetailPriceClient { FetchMySqlFlexPricesFn = (_, _) => canned };
        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => false,
            QueryCachedFn = (_, _, _, _) => canned,
        };

        var result = NewRepo(cache, client).GetMySqlFlexPrices("Standard_B1ms", "eastus", "burstable");

        Assert.Equal(0.04, result.PaygHourly);
        // fetch_mysql_flex_prices.assert_called_once_with("Standard_B1ms", "eastus")
        Assert.Equal(("Standard_B1ms", "eastus"), Assert.Single(client.FetchMySqlFlexPricesCalls));
        // delete+insert por fetch_query "mysql Standard_B1ms eastus"
        Assert.Equal("mysql Standard_B1ms eastus", Assert.Single(cache.DeleteByFetchQueryCalls));
        var insert = Assert.Single(cache.InsertCalls);
        Assert.Equal("mysql Standard_B1ms eastus", insert.FetchQuery);
        Assert.Equal(canned, insert.Rows);
    }
}
