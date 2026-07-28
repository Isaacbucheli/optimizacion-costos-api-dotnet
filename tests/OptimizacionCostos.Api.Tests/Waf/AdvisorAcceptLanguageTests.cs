using System.Net;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// ARM localiza shortDescription/label según Accept-Language. El texto que se guarda en
/// advisor_name_en tiene que ser el inglés del portal, así que el header va fijo en cada request
/// (hoy en-us es el default de ARM; esto lo deja de depender del locale del host).
/// </summary>
public sealed class AdvisorAcceptLanguageTests
{
    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<string?> AcceptLanguages { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            AcceptLanguages.Add(request.Headers.AcceptLanguage.ToString());
            return Task.FromResult(route(request));
        }
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    [Fact]
    public async Task Cada_request_ARM_pide_ingles()
    {
        var page1 = "{\"value\":[{\"name\":\"a\",\"properties\":{}}]," +
                    "\"nextLink\":\"https://management.azure.com/p?api-version=2025-01-01&$skiptoken=P2\"}";
        var page2 = "{\"value\":[{\"name\":\"b\",\"properties\":{}}]}";
        var handler = new CapturingHandler(req =>
            req.RequestUri!.Query.Contains("skiptoken") ? Ok(page2) : Ok(page1));

        var (items, _) = await AdvisorApiClient.ListAllPagesAsync(
            new HttpClient(handler), "tok", "sub-x", 200, CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal(2, handler.AcceptLanguages.Count); // ambas páginas
        Assert.All(handler.AcceptLanguages, lang => Assert.Equal("en-us", lang));
    }
}
