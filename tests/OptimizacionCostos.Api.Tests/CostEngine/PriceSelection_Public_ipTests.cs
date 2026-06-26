using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Port de la SELECCIÓN de precios del servicio "public_ip" desde
/// tests/test_paas_pricing.py (test_public_ip_selection_uses_retail_api_hourly_meter)
/// más la orquestación de caché/refresh de <c>get_public_ip_monthly_price</c>.
///
/// MONTOS EXACTOS del Python: precio horario 0.005 → mensual 0.005 × 730 = 3.65
/// (monthly_from_hourly con HOURS_PER_MONTH = 730.0).
/// </summary>
public sealed class PriceSelection_Public_ipTests
{
    private const double HoursPerMonth = 730.0;

    private static SqlPriceRepository NewRepo(FakePriceCache cache, FakeRetailPriceClient client)
        => new(cache, client, new FakePricingConstants());

    /// <summary>
    /// Port directo de test_public_ip_selection_uses_retail_api_hourly_meter: ante un meter
    /// horario "Standard IPv4 Static Public IP" (producto "IP Addresses") y un "Public IP Prefix"
    /// que NO debe elegirse, el monto mensual es 0.005 × 730 = 3.65.
    /// </summary>
    [Fact]
    public void PublicIp_Selection_UsesRetailApiHourlyMeter()
    {
        const double publicIpHourly = 0.005;
        const double prefixHourly = 0.006;
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "IP Addresses"),
                ("sku_name", "Standard"),
                ("meter_name", "Standard IPv4 Static Public IP"),
                ("price_type", "Consumption"),
                ("retail_price", publicIpHourly),
                ("unit_of_measure", "1 Hour"),
            },
            new (string, object?)[]
            {
                ("product_name", "Public IP Prefix"),
                ("sku_name", "Standard"),
                ("meter_name", "Standard Static IP Addresses"),
                ("price_type", "Consumption"),
                ("retail_price", prefixHourly),
                ("unit_of_measure", "1 Hour"),
            });

        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => true, // cache fresca → no se dispara fetch
            QueryCachedFn = (_, _, _, _) => cached,
        };
        var client = new FakeRetailPriceClient();
        var repo = NewRepo(cache, client);

        var monthly = repo.GetPublicIpMonthlyPrice("Standard", "eastus", "Static");

        Assert.Equal(publicIpHourly * HoursPerMonth, monthly); // 3.65
        Assert.Empty(client.FetchPublicIpPricesCalls); // cache fresca: no fetch
    }

    /// <summary>
    /// Cache fresca: consulta por servicio "Virtual Network" y región normalizada, sin fetch.
    /// </summary>
    [Fact]
    public void PublicIp_CacheFresh_QueriesNormalizedRegion_NoFetch()
    {
        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => true,
            QueryCachedFn = (_, _, _, _) => System.Array.Empty<PriceRow>(),
        };
        var client = new FakeRetailPriceClient();
        var repo = NewRepo(cache, client);

        repo.GetPublicIpMonthlyPrice("Standard", "East US", "Static");

        Assert.Empty(client.FetchPublicIpPricesCalls);
        var q = Assert.Single(cache.QueryCachedCalls);
        Assert.Equal("Virtual Network", q.ServiceName);
        Assert.Equal("eastus", q.Region); // lower sin espacios
        // fetch_query freshness chequeado con la región normalizada
        Assert.Contains("public_ip eastus", cache.IsFetchQueryFreshCalls);
    }

    /// <summary>
    /// Cache rancia: dispara fetch_public_ip_prices(region_normalizada), borra por fetch_query e
    /// inserta; luego selecciona del cache. El fetch recibe la región normalizada.
    /// </summary>
    [Fact]
    public void PublicIp_Stale_FetchesDeletesInserts_ThenSelects()
    {
        const double publicIpHourly = 0.005;
        var fetched = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "IP Addresses"),
                ("sku_name", "Standard"),
                ("meter_name", "Standard IPv4 Static Public IP"),
                ("price_type", "Consumption"),
                ("retail_price", publicIpHourly),
                ("unit_of_measure", "1 Hour"),
            });

        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => false, // rancio → fetch
            QueryCachedFn = (_, _, _, _) => fetched, // tras el refresh, el cache devuelve lo nuevo
        };
        var client = new FakeRetailPriceClient
        {
            FetchPublicIpPricesFn = _ => fetched,
        };
        var repo = NewRepo(cache, client);

        var monthly = repo.GetPublicIpMonthlyPrice("Standard", "East US", "Static");

        Assert.Equal(publicIpHourly * HoursPerMonth, monthly); // 3.65
        var fetchRegion = Assert.Single(client.FetchPublicIpPricesCalls);
        Assert.Equal("eastus", fetchRegion); // región normalizada
        Assert.Contains("public_ip eastus", cache.DeleteByFetchQueryCalls);
        var insert = Assert.Single(cache.InsertCalls);
        Assert.Equal("public_ip eastus", insert.FetchQuery);
    }

    /// <summary>
    /// Región vacía: devuelve null sin tocar cache ni cliente (paridad con el early-return Python).
    /// </summary>
    [Fact]
    public void PublicIp_EmptyRegion_ReturnsNull_NoCacheNoFetch()
    {
        var cache = new FakePriceCache();
        var client = new FakeRetailPriceClient();
        var repo = NewRepo(cache, client);

        var monthly = repo.GetPublicIpMonthlyPrice("Standard", "   ", "Static");

        Assert.Null(monthly);
        Assert.Empty(client.FetchPublicIpPricesCalls);
        Assert.Empty(cache.QueryCachedCalls);
        Assert.Empty(cache.IsFetchQueryFreshCalls);
    }

    /// <summary>
    /// Sin candidato "IP Addresses" que matchee (solo prefix) → null.
    /// </summary>
    [Fact]
    public void PublicIp_OnlyPrefix_ReturnsNull()
    {
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "Public IP Prefix"),
                ("sku_name", "Standard"),
                ("meter_name", "Standard Static IP Addresses"),
                ("price_type", "Consumption"),
                ("retail_price", 0.006),
                ("unit_of_measure", "1 Hour"),
            });

        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => true,
            QueryCachedFn = (_, _, _, _) => cached,
        };
        var repo = NewRepo(cache, new FakeRetailPriceClient());

        Assert.Null(repo.GetPublicIpMonthlyPrice("Standard", "eastus", "Static"));
    }

    /// <summary>
    /// Meter mensual ("1/Month"): se devuelve el precio tal cual, sin multiplicar por 730.
    /// </summary>
    [Fact]
    public void PublicIp_MonthlyMeter_ReturnedAsIs()
    {
        const double monthly = 3.60;
        var cached = PriceRowFactory.Many(
            new (string, object?)[]
            {
                ("product_name", "IP Addresses"),
                ("sku_name", "Standard"),
                ("meter_name", "Standard Static Public IP"),
                ("price_type", "Consumption"),
                ("retail_price", monthly),
                ("unit_of_measure", "1/Month"),
            });

        var cache = new FakePriceCache
        {
            IsFetchQueryFreshFn = _ => true,
            QueryCachedFn = (_, _, _, _) => cached,
        };
        var repo = NewRepo(cache, new FakeRetailPriceClient());

        Assert.Equal(monthly, repo.GetPublicIpMonthlyPrice("Standard", "eastus", "Static"));
    }
}
