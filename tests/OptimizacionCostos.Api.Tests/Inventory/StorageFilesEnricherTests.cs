using System.Net;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Features.Inventory;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Inventory;

/// <summary>
/// StorageFilesEnricher (spec 2026-07-24): lista fileshares por ARM ($expand=stats),
/// agrega por tier (estándar = GiB usados; premium = GiB de cuota), aplica el corte
/// ESTRICTO de 10 TiB (10,240 GiB) y tolera fallos por cuenta con advertencia visible.
/// HTTP mockeado con HttpMessageHandler falso; token con DelegatedTokenCredential.
/// </summary>
public sealed class StorageFilesEnricherTests
{
    private const double Gib = 1024d * 1024 * 1024;

    private static readonly TokenCredential FakeCred =
        DelegatedTokenCredential.Create((_, _) => new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static StorageFilesEnricher NewEnricher(FakeHandler handler)
        => new(new FakeHttpClientFactory(handler), NullLogger<StorageFilesEnricher>.Instance);

    private static JsonNode AccountRow(string name, string kind = "StorageV2")
        => new JsonObject
        {
            ["id"] = $"/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Storage/storageAccounts/{name}",
            ["name"] = name,
            ["kind"] = kind,
            ["skuName"] = "Standard_LRS",
        };

    private static HttpResponseMessage SharesResponse(params (long UsageBytes, int QuotaGib, string? Tier)[] shares)
    {
        var value = new JsonArray();
        foreach (var (usage, quota, tier) in shares)
        {
            value.Add(new JsonObject
            {
                ["name"] = $"share{value.Count}",
                ["properties"] = new JsonObject
                {
                    ["shareUsageBytes"] = usage,
                    ["shareQuota"] = quota,
                    ["accessTier"] = tier,
                },
            });
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject { ["value"] = value }.ToJsonString()),
        };
    }

    [Fact]
    public async Task CuentaGrandeEstandar_EntraConUsoPorTier()
    {
        // 12,000 GiB usados en hot (cuota 20,480) → supera 10,240 → entra por USO.
        var handler = new FakeHandler(_ => SharesResponse(((long)(12000 * Gib), 20480, "Hot")));
        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stgbig")], CancellationToken.None);

        var kept = Assert.Single(result.Kept);
        Assert.Empty(result.Warnings);
        Assert.Equal(1, kept.Int("shareCount"));
        Assert.Equal(12000.0, kept.Dbl("usedGib")!.Value, 1);
        Assert.Equal(20480.0, kept.Dbl("provisionedGib")!.Value, 1);
        Assert.Equal(12000.0, kept.Dbl("billableGib")!.Value, 1);
        Assert.Contains("\"hot\":", kept.Str("tierBreakdownJson"));
    }

    [Fact]
    public async Task CortePorUso_10240ExactoNoEntra_10241Entra()
    {
        var exactly = new FakeHandler(_ => SharesResponse(((long)(10240 * Gib), 102400, "Hot")));
        var over = new FakeHandler(_ => SharesResponse(((long)(10241 * Gib), 102400, "Hot")));

        Assert.Empty((await NewEnricher(exactly).EnrichAsync(FakeCred, [AccountRow("a")], default)).Kept);
        Assert.Single((await NewEnricher(over).EnrichAsync(FakeCred, [AccountRow("b")], default)).Kept);
    }

    [Fact]
    public async Task Premium_FacturaPorCuota_NoPorUso()
    {
        // Premium: 11,000 GiB de cuota con solo 100 GiB usados → entra igual (cuota manda).
        var handler = new FakeHandler(_ => SharesResponse(((long)(100 * Gib), 11000, "Premium")));
        var result = await NewEnricher(handler)
            .EnrichAsync(FakeCred, [AccountRow("stgprem", kind: "FileStorage")], default);

        var kept = Assert.Single(result.Kept);
        Assert.Equal(11000.0, kept.Dbl("billableGib")!.Value, 1);
        Assert.Contains("\"premium\":", kept.Str("tierBreakdownJson"));
    }

    [Fact]
    public async Task EstandarSinTier_CaeATransactionOptimized()
    {
        var handler = new FakeHandler(_ => SharesResponse(((long)(11000 * Gib), 20480, null)));
        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stg")], default);
        Assert.Contains("\"transaction_optimized\":", Assert.Single(result.Kept).Str("tierBreakdownJson"));
    }

    [Fact]
    public async Task SinShares_NoSeInventariaNiAdvierte()
    {
        var handler = new FakeHandler(_ => SharesResponse());
        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stgblob")], default);
        Assert.Empty(result.Kept);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task FalloArmPorCuenta_OmiteConAdvertencia_YSigueConLasDemas()
    {
        var handler = new FakeHandler(req =>
            req.RequestUri!.ToString().Contains("stgmala")
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : SharesResponse(((long)(12000 * Gib), 20480, "Hot")));

        var result = await NewEnricher(handler)
            .EnrichAsync(FakeCred, [AccountRow("stgmala"), AccountRow("stgbuena")], default);

        Assert.Single(result.Kept);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("stgmala", warning);
    }

    [Fact]
    public async Task Paginacion_SigueNextLink()
    {
        var page2Url = "https://management.azure.com/page2";
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.ToString() == page2Url)
            {
                return SharesResponse(((long)(6000 * Gib), 10240, "Hot"));
            }
            var first = SharesResponse(((long)(6000 * Gib), 10240, "Hot"));
            var body = JsonNode.Parse(first.Content.ReadAsStringAsync().Result)!.AsObject();
            body["nextLink"] = page2Url;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
        });

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stg")], default);
        var kept = Assert.Single(result.Kept); // 12,000 GiB entre las 2 páginas → entra
        Assert.Equal(2, kept.Int("shareCount"));
        Assert.Equal(12000.0, kept.Dbl("billableGib")!.Value, 1);
    }

    [Fact]
    public async Task UsaApiVersionYExpandStats()
    {
        var handler = new FakeHandler(_ => SharesResponse(((long)(11000 * Gib), 20480, "Hot")));
        await NewEnricher(handler).EnrichAsync(FakeCred, [AccountRow("stg")], default);
        var url = Assert.Single(handler.Urls);
        Assert.Contains("/fileServices/default/shares", url);
        Assert.Contains("api-version=2023-05-01", url);
        Assert.Contains("expand=stats", url, StringComparison.OrdinalIgnoreCase);
    }
}
