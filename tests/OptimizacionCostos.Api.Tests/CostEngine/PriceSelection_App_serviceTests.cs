using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Port de los tests de SELECCIÓN de precios del servicio "app_service" de
/// tests/test_paas_pricing.py:
///   - test_app_service_selection_separates_linux_windows_and_keeps_ri
///   - test_app_service_p0v3_linux_uses_linux_reservation
///   - test_app_service_repository_uses_sku_name_for_cache_lookup
///   - test_app_service_repository_refreshes_ri_from_api_without_sku_exception
///   - test_elastic_premium_base_price_combines_vcpu_and_memory
///
/// Los selectores _select_app_service_prices / _select_duration_unit_prices son privados en
/// C#; se ejercitan a través de GetAppServicePrices / GetElasticPremiumBasePrice. Para los
/// tests de pura selección, IsCacheFreshFor=true → no se dispara fetch y QueryCached devuelve
/// los arrays canned (idéntico a llamar al selector directo en Python). Montos EXACTOS del Python.
///
/// El fallback de IA (_assistant_select) NO se porta; cuando la selección determinista no halla
/// PAYG, payg_hourly queda null, igual que con assist deshabilitado.
/// </summary>
public sealed class PriceSelection_App_serviceTests
{
    private static SqlPriceRepository MakeRepo(FakePriceCache cache, FakeRetailPriceClient client)
        => new(cache, client, new FakePricingConstants());

    [Fact]
    public void AppService_Selection_SeparatesLinuxWindows_AndKeepsRi()
    {
        const double linuxPayg = 0.20;
        const double windowsPayg = 0.18;
        const double linuxRi1y = 800.0;
        const double linuxRi3y = 2000.0;
        const double windowsRi1y = 1000.0;
        const double windowsRi3y = 2400.0;

        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Linux"), ("sku_name", "P1v3"), ("meter_name", "P1 v3"),
                ("meter_id", "meter-appservice-linux-payg"),
                ("price_type", "Consumption"), ("retail_price", linuxPayg), ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service"), ("sku_name", "P1v3"), ("meter_name", "P1 v3"),
                ("meter_id", "meter-appservice-payg"),
                ("price_type", "Consumption"), ("retail_price", windowsPayg), ("unit_of_measure", "1 Hour"),
                ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Linux"), ("sku_name", "P1v3"), ("meter_name", "P1 v3"),
                ("price_type", "Reservation"), ("reservation_term", "1 Year"), ("retail_price", linuxRi1y),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Linux"), ("sku_name", "P1v3"), ("meter_name", "P1 v3"),
                ("price_type", "Reservation"), ("reservation_term", "3 Years"), ("retail_price", linuxRi3y),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service"), ("sku_name", "P1v3"), ("meter_name", "P1 v3"),
                ("price_type", "Reservation"), ("reservation_term", "1 Year"), ("retail_price", windowsRi1y),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service"), ("sku_name", "P1v3"), ("meter_name", "P1 v3"),
                ("price_type", "Reservation"), ("reservation_term", "3 Years"), ("retail_price", windowsRi3y),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service"), ("meter_name", "Certificate"),
                ("price_type", "Consumption"), ("retail_price", 99.0),
            });

        var cache = new FakePriceCache { QueryCachedFn = (_, _, _, _) => cached };
        var repo = MakeRepo(cache, new FakeRetailPriceClient());

        var linux = repo.GetAppServicePrices("P1v3", "eastus2", isLinux: true);
        var windows = repo.GetAppServicePrices("P1v3", "eastus2", isLinux: false);

        Assert.Equal(linuxPayg, linux.PaygHourly);
        Assert.Equal(windowsPayg, windows.PaygHourly);
        Assert.Equal(linuxRi1y, linux.Ri1yTotal);
        Assert.Equal(windowsRi3y, windows.Ri3yTotal);
        Assert.Equal("meter-appservice-linux-payg", linux.PaygMeterId);
        Assert.Equal("meter-appservice-payg", windows.PaygMeterId);
    }

    [Fact]
    public void AppService_P0v3_Linux_UsesLinuxReservation()
    {
        const double linuxPayg = 0.07123;
        const double linuxRi1y = 431.25;
        const double linuxRi3y = 918.75;
        const double windowsRi1y = 1008.5;
        const double windowsRi3y = 2488.25;
        const double otherSkuPayg = 0.25;
        const double otherSkuRi1y = 1500.0;

        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Premium v3 Plan - Linux"), ("sku_name", "P0v3"),
                ("meter_name", "P0v3 App"), ("price_type", "Consumption"), ("retail_price", linuxPayg),
                ("unit_of_measure", "1 Hour"), ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Premium v3 Plan - Linux"), ("sku_name", "P0v3"),
                ("meter_name", "P0v3 App"), ("price_type", "Reservation"), ("reservation_term", "1 Year"),
                ("retail_price", linuxRi1y),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Premium v3 Plan - Linux"), ("sku_name", "P0v3"),
                ("meter_name", "P0v3 App"), ("price_type", "Reservation"), ("reservation_term", "3 Years"),
                ("retail_price", linuxRi3y),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Premium v3 Plan"), ("sku_name", "P0v3"),
                ("meter_name", "P0v3 App"), ("price_type", "Reservation"), ("reservation_term", "1 Year"),
                ("retail_price", windowsRi1y),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Premium v3 Plan"), ("sku_name", "P0v3"),
                ("meter_name", "P0v3 App"), ("price_type", "Reservation"), ("reservation_term", "3 Years"),
                ("retail_price", windowsRi3y),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Premium v3 Plan - Linux"), ("sku_name", "P1v3"),
                ("meter_name", "P1v3 App"), ("price_type", "Consumption"), ("retail_price", otherSkuPayg),
                ("unit_of_measure", "1 Hour"), ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Premium v3 Plan - Linux"), ("sku_name", "P1v3"),
                ("meter_name", "P1v3 App"), ("price_type", "Reservation"), ("reservation_term", "1 Year"),
                ("retail_price", otherSkuRi1y),
            });

        var cache = new FakePriceCache { QueryCachedFn = (_, _, _, _) => cached };
        var repo = MakeRepo(cache, new FakeRetailPriceClient());

        var linux = repo.GetAppServicePrices("P0v3", "eastus2", isLinux: true);

        Assert.Equal(linuxPayg, linux.PaygHourly);
        Assert.Equal(linuxRi1y, linux.Ri1yTotal);
        Assert.Equal(linuxRi3y, linux.Ri3yTotal);
    }

    [Fact]
    public void AppService_Repository_UsesSkuNameForCacheLookup()
    {
        var item = PriceRowFactory.Of(
            ("product_name", "Azure App Service Basic Plan"),
            ("sku_name", "B1"),
            ("arm_sku_name", ""),
            ("meter_name", "B1 App"),
            ("price_type", "Consumption"),
            ("retail_price", 0.075),
            ("unit_of_measure", "1 Hour"));
        var rows = new[] { item };

        var cache = new FakePriceCache
        {
            IsCacheFreshForFn = (_, _, _, _) => false,
            QueryCachedFn = (_, _, _, _) => rows,
        };
        var client = new FakeRetailPriceClient { FetchAppServicePricesFn = (_, _, _) => rows };
        var repo = MakeRepo(cache, client);

        var selected = repo.GetAppServicePrices("B1", "eastus2", isLinux: false);

        Assert.Equal(0.075, selected.PaygHourly);

        // is_fresh.assert_called_once_with("Azure App Service", "eastus2", sku_name="B1")
        var freshCall = Assert.Single(cache.IsCacheFreshForCalls);
        Assert.Equal(("Azure App Service", "eastus2", (string?)null, (string?)"B1"), freshCall);

        // fetch_prices.call_count == 2 (Windows + Linux)
        Assert.Equal(2, client.FetchAppServicePricesCalls.Count);

        // delete_stale.assert_called_once_with("Azure App Service", "eastus2", sku_name="B1")
        var deleteCall = Assert.Single(cache.DeleteStaleCalls);
        Assert.Equal(("Azure App Service", "eastus2", (string?)null, (string?)"B1"), deleteCall);

        // insert_batch.assert_called_once()
        Assert.Single(cache.InsertCalls);

        // query_cached.assert_called_once_with("Azure App Service", "eastus2", sku_name="B1")
        var queryCall = Assert.Single(cache.QueryCachedCalls);
        Assert.Equal(("Azure App Service", "eastus2", (string?)null, (string?)"B1"), queryCall);
    }

    [Fact]
    public void AppService_Repository_RefreshesRiFromApi_WithoutSkuException()
    {
        const string skuName = "P0v3";
        const double linuxPayg = 0.07123;
        const double linuxRi1y = 431.25;
        const double linuxRi3y = 918.75;
        const double windowsRi1y = 1008.5;

        var initialCache = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Premium v3 Plan - Linux"), ("sku_name", skuName),
                ("meter_name", "P0v3 App"), ("price_type", "Consumption"), ("retail_price", linuxPayg),
                ("unit_of_measure", "1 Hour"), ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Premium v3 Plan"), ("sku_name", skuName),
                ("meter_name", "P0v3 App"), ("price_type", "Reservation"), ("reservation_term", "1 Year"),
                ("retail_price", windowsRi1y),
            });

        var refreshedCache = initialCache.Concat(PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Premium v3 Plan - Linux"), ("sku_name", skuName),
                ("meter_name", "P0v3 App"), ("price_type", "Reservation"), ("reservation_term", "1 Year"),
                ("retail_price", linuxRi1y),
            },
            new (string, object?)[]
            {
                ("product_name", "Azure App Service Premium v3 Plan - Linux"), ("sku_name", skuName),
                ("meter_name", "P0v3 App"), ("price_type", "Reservation"), ("reservation_term", "3 Years"),
                ("retail_price", linuxRi3y),
            })).ToList();

        IReadOnlyList<PriceRow> FetchAppService(string _sku, string _region, bool isLinux)
        {
            if (isLinux)
            {
                return PriceRowFactory.Many(
                    new (string, object?)[]
                    {
                        ("meterId", "linux-payg"), ("productName", "Azure App Service Premium v3 Plan - Linux"),
                        ("skuName", skuName), ("meterName", "P0v3 App"), ("type", "Consumption"),
                        ("retailPrice", linuxPayg),
                    },
                    new (string, object?)[]
                    {
                        ("meterId", "linux-ri-1y"), ("productName", "Azure App Service Premium v3 Plan - Linux"),
                        ("skuName", skuName), ("meterName", "P0v3 App"), ("type", "Reservation"),
                        ("reservationTerm", "1 Year"), ("retailPrice", linuxRi1y),
                    },
                    new (string, object?)[]
                    {
                        ("meterId", "linux-ri-3y"), ("productName", "Azure App Service Premium v3 Plan - Linux"),
                        ("skuName", skuName), ("meterName", "P0v3 App"), ("type", "Reservation"),
                        ("reservationTerm", "3 Years"), ("retailPrice", linuxRi3y),
                    });
            }

            return PriceRowFactory.Many(
                new (string, object?)[]
                {
                    ("meterId", "windows-ri-1y"), ("productName", "Azure App Service Premium v3 Plan"),
                    ("skuName", skuName), ("meterName", "P0v3 App"), ("type", "Reservation"),
                    ("reservationTerm", "1 Year"), ("retailPrice", windowsRi1y),
                });
        }

        // _is_cache_fresh_for=True (no refresh inicial); _query_cached side_effect=[initial, refreshed].
        var cache = new FakePriceCache { IsCacheFreshForFn = (_, _, _, _) => true };
        cache.QueueCached(initialCache, refreshedCache);
        var client = new FakeRetailPriceClient { FetchAppServicePricesFn = FetchAppService };
        var repo = MakeRepo(cache, client);

        var selected = repo.GetAppServicePrices(skuName, "eastus2", isLinux: true);

        Assert.Equal(linuxPayg, selected.PaygHourly);
        Assert.Equal(linuxRi1y, selected.Ri1yTotal);
        Assert.Equal(linuxRi3y, selected.Ri3yTotal);

        // fetch_prices.call_count == 2 (re-fetch dirigido: Windows + Linux)
        Assert.Equal(2, client.FetchAppServicePricesCalls.Count);

        // delete_stale.assert_called_once_with("Azure App Service", "eastus2", sku_name=sku_name)
        var deleteCall = Assert.Single(cache.DeleteStaleCalls);
        Assert.Equal(("Azure App Service", "eastus2", (string?)null, (string?)skuName), deleteCall);

        // insert_batch.assert_called_once()
        Assert.Single(cache.InsertCalls);
    }

    [Fact]
    public void ElasticPremiumBasePrice_CombinesVcpuAndMemory()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Premium Functions"), ("sku_name", "Premium"),
                ("meter_name", "Premium vCPU Duration"), ("price_type", "Consumption"),
                ("retail_price", 0.16), ("unit_of_measure", "1 Hour"), ("is_primary_meter", true),
            },
            new (string, object?)[]
            {
                ("product_name", "Premium Functions"), ("sku_name", "Premium"),
                ("meter_name", "Premium Memory Duration"), ("price_type", "Consumption"),
                ("retail_price", 0.0114), ("unit_of_measure", "1 GiB Hour"), ("is_primary_meter", true),
            });

        // _is_fetch_query_fresh=True (no fetch); _query_cached devuelve los meters de duración.
        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => true,
            QueryCachedFn = (_, _, _, _) => cached,
        };
        var repo = MakeRepo(cache, new FakeRetailPriceClient());

        var ep1 = repo.GetElasticPremiumBasePrice("EP1", "eastus2");

        Assert.NotNull(ep1);
        // EP1 = 1 vCPU + 3.5 GiB
        Assert.Equal(1 * 0.16 + 3.5 * 0.0114, ep1!.BaseHourly, 10);
        Assert.Equal(1, ep1.Vcpu);
    }
}
