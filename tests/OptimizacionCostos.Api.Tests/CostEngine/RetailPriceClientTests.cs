using System.Net;
using System.Text;
using System.Web;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Tests de <see cref="RetailPriceClient"/> (port de app/pricing/azure_retail_client.py).
/// Mockean el <c>HttpMessageHandler</c> para devolver JSON canned y verifican:
///   - el $filter OData construido por cada fetch_*,
///   - el mapeo de cada item JSON a <see cref="PriceRow"/> (incluido "type" -> PriceType),
///   - la paginación por NextPageLink hasta agotar resultados,
///   - los filtrados post-fetch por productName / OS / capacity.
///
/// Incluye el port directo de test_app_service_fetch_uses_sku_name_candidates.
/// </summary>
public sealed class RetailPriceClientTests
{
    // -------------------- Infraestructura de mock HTTP --------------------

    /// <summary>Handler que registra las URLs solicitadas y devuelve respuestas en cola.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public List<Uri> RequestedUris { get; } = new();

        public StubHandler(params string[] jsonBodies)
        {
            _responses = new Queue<string>(jsonBodies);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            var body = _responses.Count > 0 ? _responses.Dequeue() : "{\"Items\":[]}";
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }

    private static (RetailPriceClient Client, StubHandler Handler) NewClient(params string[] jsonBodies)
    {
        var handler = new StubHandler(jsonBodies);
        var http = new HttpClient(handler);
        return (new RetailPriceClient(http), handler);
    }

    /// <summary>Extrae el valor del query param $filter de la primera petición.</summary>
    private static string FilterOf(StubHandler handler)
    {
        var q = HttpUtility.ParseQueryString(handler.RequestedUris[0].Query);
        return q["$filter"] ?? "";
    }

    private static string CurrencyOf(StubHandler handler)
    {
        var q = HttpUtility.ParseQueryString(handler.RequestedUris[0].Query);
        return q["currencyCode"] ?? "";
    }

    private static string Page(string itemsJson, string? next = null)
    {
        var nextPart = next is null ? "null" : $"\"{next}\"";
        return $"{{\"Items\":[{itemsJson}],\"NextPageLink\":{nextPart}}}";
    }

    // =====================================================================================
    //  BuildFilter (port de _build_filter)
    // =====================================================================================

    [Fact]
    public void BuildFilter_ScalarsListsBoolsAndNumbers()
    {
        var filter = RetailPriceClient.BuildFilter(new KeyValuePair<string, object>[]
        {
            new("serviceName", "Virtual Machines"),
            new("armSkuName", (object)new[] { "Standard_D2s_v5", "Standard_D4s_v5" }),
            new("isPrimaryMeter", true),
            new("tierMinimumUnits", 0),
        });

        Assert.Equal(
            "serviceName eq 'Virtual Machines' and " +
            "(armSkuName eq 'Standard_D2s_v5' or armSkuName eq 'Standard_D4s_v5') and " +
            "isPrimaryMeter eq true and " +
            "tierMinimumUnits eq 0",
            filter);
    }

    // =====================================================================================
    //  Mapeo de item JSON -> PriceRow + currencyCode + paginación
    // =====================================================================================

    [Fact]
    public void Fetch_MapsJsonItemToPriceRow_AndSendsCurrencyUsd()
    {
        const string item = """
        {
          "serviceName":"Virtual Machines","productName":"Virtual Machines Dadsv5 Series",
          "skuName":"D2as v5","armSkuName":"Standard_D2as_v5","meterName":"D2as v5",
          "meterId":"abc-123","armRegionName":"eastus2","type":"Consumption",
          "reservationTerm":null,"currencyCode":"USD","unitPrice":0.10,"retailPrice":0.12,
          "unitOfMeasure":"1 Hour","tierMinimumUnits":0.0,"isPrimaryMeter":true
        }
        """;
        var (client, handler) = NewClient(Page(item));

        var rows = client.FetchVmRegionPrices("eastus2");

        Assert.Single(rows);
        var r = rows[0];
        Assert.Equal("Virtual Machines", r.ServiceName);
        Assert.Equal("Virtual Machines Dadsv5 Series", r.ProductName);
        Assert.Equal("D2as v5", r.SkuName);
        Assert.Equal("Standard_D2as_v5", r.ArmSkuName);
        Assert.Equal("D2as v5", r.MeterName);
        Assert.Equal("abc-123", r.MeterId);
        Assert.Equal("eastus2", r.ArmRegionName);
        Assert.Equal("Consumption", r.PriceType);          // "type" -> PriceType
        Assert.True(r.IsConsumption);
        Assert.Equal("USD", r.CurrencyCode);
        Assert.Equal(0.10, r.UnitPrice);
        Assert.Equal(0.12, r.RetailPrice);
        Assert.Equal("1 Hour", r.UnitOfMeasure);
        Assert.True(r.IsPrimaryMeter);

        Assert.Equal("USD", CurrencyOf(handler));
        Assert.Equal("serviceName eq 'Virtual Machines' and armRegionName eq 'eastus2'", FilterOf(handler));
    }

    [Fact]
    public void Fetch_FollowsNextPageLinkUntilExhausted()
    {
        var p1 = Page("""{"skuName":"A","type":"Consumption"}""", next: "https://prices.azure.com/api/retail/prices?page=2");
        var p2 = Page("""{"skuName":"B","type":"Consumption"}""", next: null);
        var (client, handler) = NewClient(p1, p2);

        var rows = client.FetchVmRegionPrices("eastus2");

        Assert.Equal(2, rows.Count);
        Assert.Equal("A", rows[0].SkuName);
        Assert.Equal("B", rows[1].SkuName);
        Assert.Equal(2, handler.RequestedUris.Count);
        // La 2da petición usa la NextPageLink tal cual (no re-arma query).
        Assert.Contains("page=2", handler.RequestedUris[1].Query);
    }

    [Fact]
    public void Fetch_PropagatesHttpError()
    {
        var handler = new ThrowingHandler();
        var client = new RetailPriceClient(new HttpClient(handler));
        Assert.ThrowsAny<Exception>(() => client.FetchVmRegionPrices("eastus2"));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    // =====================================================================================
    //  App Service: port de test_app_service_fetch_uses_sku_name_candidates
    // =====================================================================================

    [Fact]
    public void FetchAppServicePrices_UsesSkuNameCandidates_AndRenormalizesSkuName()
    {
        const string apiItem = """
        {"productName":"Azure App Service Premium v3 Plan","skuName":"P2 v3","meterName":"P2 v3 App"}
        """;
        var (client, handler) = NewClient(Page(apiItem));

        var items = client.FetchAppServicePrices("P2v3", "eastus2", isLinux: false);

        var filter = FilterOf(handler);
        Assert.Contains("serviceName eq 'Azure App Service'", filter);
        Assert.Contains("armRegionName eq 'eastus2'", filter);
        // skuName como lista de candidatos: "P2v3" y "P2 v3".
        Assert.Contains("(skuName eq 'P2v3' or skuName eq 'P2 v3')", filter);
        Assert.DoesNotContain("armSkuName", filter);
        // El skuName devuelto se re-normaliza al original "P2v3".
        Assert.Single(items);
        Assert.Equal("P2v3", items[0].SkuName);
    }

    [Fact]
    public void FetchAppServicePrices_D1AddsSharedCandidate()
    {
        var (client, handler) = NewClient(Page(""));
        client.FetchAppServicePrices("D1", "eastus2");
        Assert.Contains("(skuName eq 'D1' or skuName eq 'Shared')", FilterOf(handler));
    }

    [Fact]
    public void FetchAppServicePrices_SeparatesLinuxFromWindows()
    {
        var linux = """{"productName":"Azure App Service Premium v3 Plan Linux","skuName":"P1v3"}""";
        var windows = """{"productName":"Azure App Service Premium v3 Plan","skuName":"P1v3"}""";
        var (client, _) = NewClient(Page($"{linux},{windows}"));

        var linuxItems = client.FetchAppServicePrices("P1v3", "eastus2", isLinux: true);
        Assert.Single(linuxItems);
        Assert.Contains("Linux", linuxItems[0].ProductName);

        var (client2, _) = NewClient(Page($"{linux},{windows}"));
        var winItems = client2.FetchAppServicePrices("P1v3", "eastus2", isLinux: false);
        Assert.Single(winItems);
        Assert.DoesNotContain("Linux", winItems[0].ProductName);
    }

    // =====================================================================================
    //  VM: filtro por OS en memoria + filtro armSkuName como lista
    // =====================================================================================

    [Fact]
    public void FetchVmPrices_FiltersWindowsByProductName_AndSendsArmSkuList()
    {
        var win = """{"productName":"Virtual Machines Das v5 Series Windows","armSkuName":"Standard_D2as_v5"}""";
        var lin = """{"productName":"Virtual Machines Das v5 Series","armSkuName":"Standard_D2as_v5"}""";
        var (client, handler) = NewClient(Page($"{win},{lin}"));

        var items = client.FetchVmPrices(new[] { "Standard_D2as_v5", "Standard_D4as_v5" }, "eastus2", "Windows");

        Assert.Single(items);
        Assert.Contains("Windows", items[0].ProductName);
        var filter = FilterOf(handler);
        Assert.Contains("serviceName eq 'Virtual Machines'", filter);
        Assert.Contains("(armSkuName eq 'Standard_D2as_v5' or armSkuName eq 'Standard_D4as_v5')", filter);
    }

    [Fact]
    public void FetchVmPrices_LinuxExcludesWindowsProducts()
    {
        var win = """{"productName":"Virtual Machines Das v5 Series Windows"}""";
        var lin = """{"productName":"Virtual Machines Das v5 Series"}""";
        var (client, _) = NewClient(Page($"{win},{lin}"));

        var items = client.FetchVmPrices(new[] { "Standard_D2as_v5" }, "eastus2", "Linux");

        Assert.Single(items);
        Assert.DoesNotContain("Windows", items[0].ProductName);
    }

    // =====================================================================================
    //  Filtrados post-fetch por productName
    // =====================================================================================

    [Fact]
    public void FetchManagedDiskPrices_KeepsOnlyManagedDisks_AndFiltersBySkuName()
    {
        var disk = """{"productName":"Premium SSD Managed Disks","meterName":"P10 LRS Disk"}""";
        var noise = """{"productName":"Standard Page Blob","meterName":"P10 LRS Disk Operations"}""";
        var (client, handler) = NewClient(Page($"{disk},{noise}"));

        var items = client.FetchManagedDiskPrices("P10 LRS", "eastus2");

        Assert.Single(items);
        Assert.Contains("Managed Disks", items[0].ProductName);
        var filter = FilterOf(handler);
        Assert.Contains("serviceName eq 'Storage'", filter);
        Assert.Contains("skuName eq 'P10 LRS'", filter);
    }

    [Fact]
    public void FetchPublicIpPrices_KeepsOnlyIpAddressesProduct()
    {
        var ip = """{"productName":"IP Addresses","skuName":"Standard"}""";
        var prefix = """{"productName":"Public IP Prefix","skuName":"Standard"}""";
        var (client, handler) = NewClient(Page($"{ip},{prefix}"));

        var items = client.FetchPublicIpPrices("eastus2");

        Assert.Single(items);
        Assert.Equal("IP Addresses", items[0].ProductName);
        Assert.Contains("serviceName eq 'Virtual Network'", FilterOf(handler));
    }

    [Fact]
    public void FetchSynapseDwPrices_KeepsOnlyDedicatedPoolAndProvisionedDwu()
    {
        var pool = """{"productName":"Azure Synapse Analytics Dedicated SQL Pool"}""";
        var dwu = """{"productName":"Azure Synapse Analytics SQL Provisioned DWU"}""";
        var other = """{"productName":"Azure Synapse Analytics Serverless"}""";
        var (client, handler) = NewClient(Page($"{pool},{dwu},{other}"));

        var items = client.FetchSynapseDwPrices("eastus2");

        Assert.Equal(2, items.Count);
        Assert.Contains("Azure Synapse Analytics", FilterOf(handler));
    }

    [Fact]
    public void FetchElasticPoolPrices_KeepsOnlyElasticPoolProducts()
    {
        var pool = """{"productName":"SQL Database Elastic Pool - Standard"}""";
        var single = """{"productName":"SQL Database Single Standard"}""";
        var (client, _) = NewClient(Page($"{pool},{single}"));

        var items = client.FetchElasticPoolPrices("eastus2");

        Assert.Single(items);
        Assert.Contains("Elastic Pool", items[0].ProductName);
    }

    [Fact]
    public void FetchMySqlFlexPrices_AddsArmSkuForBurstable_AndKeepsFlexibleServer()
    {
        var flex = """{"productName":"Azure Database for MySQL Flexible Server"}""";
        var other = """{"productName":"Azure Database for MySQL Single Server"}""";
        var (client, handler) = NewClient(Page($"{flex},{other}"));

        var items = client.FetchMySqlFlexPrices("Standard_B1ms", "eastus");

        Assert.Single(items);
        Assert.Contains("Flexible Server", items[0].ProductName);
        var filter = FilterOf(handler);
        Assert.Contains("serviceName eq 'Azure Database for MySQL'", filter);
        // "Standard_B1ms" -> armSkuName "B1MS".
        Assert.Contains("armSkuName eq 'B1MS'", filter);
    }

    [Fact]
    public void FetchMySqlStoragePrice_RequiresFlexibleServerAndStorageMeter()
    {
        var storage = """{"productName":"Azure Database for MySQL Flexible Server","meterName":"Storage"}""";
        var compute = """{"productName":"Azure Database for MySQL Flexible Server","meterName":"vCore"}""";
        var (client, _) = NewClient(Page($"{storage},{compute}"));

        var items = client.FetchMySqlStoragePrice("eastus");

        Assert.Single(items);
        Assert.Contains("Storage", items[0].MeterName);
    }

    [Fact]
    public void FetchAzureManagedRedisPrices_KeepsEnterpriseAndManagedRedis()
    {
        var ent = """{"productName":"Azure Cache for Redis Enterprise"}""";
        var mgd = """{"productName":"Azure Managed Redis"}""";
        var classic = """{"productName":"Azure Cache for Redis Standard"}""";
        var (client, handler) = NewClient(Page($"{ent},{mgd},{classic}"));

        var items = client.FetchAzureManagedRedisPrices("eastus");

        Assert.Equal(2, items.Count);
        Assert.Contains("serviceName eq 'Azure Cache for Redis'", FilterOf(handler));
    }

    [Fact]
    public void FetchRedisPrices_UsesRedisCacheServiceName()
    {
        var (client, handler) = NewClient(Page(""));
        client.FetchRedisPrices("Standard", "eastus");
        Assert.Contains("serviceName eq 'Redis Cache'", FilterOf(handler));
    }

    // =====================================================================================
    //  SQL Database: omisión de skuName para Basic / GP_S_Gen5
    // =====================================================================================

    [Fact]
    public void FetchSqlDbPrices_OmitsSkuNameForBasicAndServerless()
    {
        var (c1, h1) = NewClient(Page(""));
        c1.FetchSqlDbPrices("eastus", "Basic");
        Assert.DoesNotContain("skuName", FilterOf(h1));

        var (c2, h2) = NewClient(Page(""));
        c2.FetchSqlDbPrices("eastus", "GP_S_Gen5");
        Assert.DoesNotContain("skuName", FilterOf(h2));

        var (c3, h3) = NewClient(Page(""));
        c3.FetchSqlDbPrices("eastus", "GP_Gen5_2");
        Assert.Contains("skuName eq 'GP_Gen5_2'", FilterOf(h3));

        var (c4, h4) = NewClient(Page(""));
        c4.FetchSqlDbPrices("eastus");
        Assert.DoesNotContain("skuName", FilterOf(h4));
    }
}
