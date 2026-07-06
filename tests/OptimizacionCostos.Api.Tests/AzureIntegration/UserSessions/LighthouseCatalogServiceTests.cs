using System.Net;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.AzureIntegration.UserSessions;

namespace OptimizacionCostos.Api.Tests.AzureIntegration.UserSessions;

public sealed class FakeSessionService(TokenCredential cred) : IAzureUserSessionService
{
    public UserSessionSnapshot Start(string email) => throw new NotImplementedException();
    public UserSessionSnapshot? GetStatus(string email) => new("authenticated", null, null, null, null);
    public void Disconnect(string email) { }
    public TokenCredential GetCredentialForEmail(string email) => cred;
}

public class LighthouseCatalogServiceTests
{
    private const string BitTenant = "24884996-6863-4925-a1ba-f1a160b581e2";
    private int _httpCalls;

    private LighthouseCatalogService Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var cred = new FakeTokenCredential(new AccessToken("tok", DateTimeOffset.UtcNow.AddHours(1)));
        var handler = new CannedHandler(req => { Interlocked.Increment(ref _httpCalls); return respond(req); });
        return new LighthouseCatalogService(
            new FakeSessionService(cred), new FakeHttpFactory(handler),
            new AppConfig { UserSessionTenantId = BitTenant },
            NullLogger<LighthouseCatalogService>.Instance);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Respond(HttpRequestMessage req)
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("/subscriptions?api-version") && !url.Contains("skip"))
            return Json("""
                {"value":[
                    {"subscriptionId":"s1","displayName":"Microsoft Azure","homeTenantId":"tenant-cliente-a"},
                    {"subscriptionId":"s2","displayName":"PROD","homeTenantId":"tenant-cliente-a"}
                 ],
                 "nextLink":"https://management.azure.com/subscriptions?api-version=2020-01-01&skip=1"}
                """);
        if (url.Contains("skip=1"))
            return Json("""
                {"value":[
                    {"subscriptionId":"s3","displayName":"Sub BIT","homeTenantId":"24884996-6863-4925-a1ba-f1a160b581e2"},
                    {"subscriptionId":"s4","displayName":"Azure sub","homeTenantId":"tenant-cliente-b"}
                 ]}
                """);
        if (url.Contains("/subscriptions/s1/providers/Microsoft.ManagedServices"))
            return Json("""{"value":[{"properties":{"manageeTenantName":"SG CONSULTING GROUP","manageeTenantId":"tenant-cliente-a"}}]}""");
        if (url.Contains("/subscriptions/s4/providers/Microsoft.ManagedServices"))
            return new HttpResponseMessage(HttpStatusCode.Forbidden); // sin definición legible
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Agrupa_por_tenant_resuelve_nombres_y_ordena()
    {
        var svc = Build(Respond);
        var groups = await svc.GetClientsAsync("isaac@bit.ec");

        Assert.Equal(3, groups.Count);
        // Orden alfabético por nombre de cliente:
        Assert.StartsWith("(sin nombre, tenant tenant-cl", groups[0].ClientName); // fallback cliente B
        Assert.Equal("Business IT (tenant propio)", groups[1].ClientName);
        Assert.Equal("SG CONSULTING GROUP", groups[2].ClientName);

        var sg = groups[2];
        Assert.Equal("tenant-cliente-a", sg.TenantId);
        Assert.Equal(["s1", "s2"], sg.Subscriptions.Select(s => s.SubscriptionId).ToArray());
    }

    [Fact]
    public async Task Cachea_por_email_y_refresh_lo_ignora()
    {
        var svc = Build(Respond);
        await svc.GetClientsAsync("isaac@bit.ec");
        var callsAfterFirst = _httpCalls;

        await svc.GetClientsAsync("isaac@bit.ec");            // cache: sin HTTP nuevo
        Assert.Equal(callsAfterFirst, _httpCalls);

        await svc.GetClientsAsync("isaac@bit.ec", refresh: true); // bypass
        Assert.True(_httpCalls > callsAfterFirst);
    }
}
