using System.Net;
using System.Text.Json;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Paginación del listado de Advisor (AdvisorApiClient.ListAllPagesAsync). Cubre el caso real de
/// Servientrega PRD: la página 1 devuelve recomendaciones + un nextLink que luego 404ea. El listado
/// debe conservar la página 1 (parcial) y NO fallar la suscripción entera.
/// </summary>
public sealed class AdvisorPaginationTests
{
    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(route(request));
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent("The resource you are looking for has been removed...") };

    private const string TwoItemsWithNext =
        "{\"value\":[{\"name\":\"a\",\"properties\":{\"category\":\"Cost\"}}," +
        "{\"name\":\"b\",\"properties\":{\"category\":\"Security\"}}]," +
        "\"nextLink\":\"https://management.azure.com/nextpage?api-version=2025-01-01&$skiptoken=P2\"}";

    [Fact]
    public async Task NextLink_roto_404_conserva_la_primera_pagina_sin_fallar()
    {
        var http = new HttpClient(new RouteHandler(req =>
            req.RequestUri!.Query.Contains("skiptoken") ? NotFound() : Ok(TwoItemsWithNext)));

        var (items, truncated) = await AdvisorApiClient.ListAllPagesAsync(http, "tok", "sub-x", 200, CancellationToken.None);

        Assert.Equal(2, items.Count); // solo la página 1, pero sin excepción
        Assert.True(truncated); // el nextLink roto marca resultado parcial
    }

    [Fact]
    public async Task Primera_pagina_404_reintenta_con_version_estable()
    {
        // 2025-01-01 (versión de generate) → 404; 2023-01-01 (fallback) → 200 con un item, sin nextLink.
        var http = new HttpClient(new RouteHandler(req =>
            req.RequestUri!.Query.Contains("2025-01-01")
                ? NotFound()
                : Ok("{\"value\":[{\"name\":\"a\",\"properties\":{\"category\":\"Cost\"}}]}")));

        var (items, truncated) = await AdvisorApiClient.ListAllPagesAsync(http, "tok", "sub-x", 200, CancellationToken.None);

        Assert.Single(items);
        Assert.False(truncated); // fallback a versión estable no es truncamiento
    }

    [Fact]
    public async Task Primera_pagina_404_en_ambas_versiones_lanza()
    {
        var http = new HttpClient(new RouteHandler(_ => NotFound()));

        await Assert.ThrowsAsync<AdvisorApiException>(
            () => AdvisorApiClient.ListAllPagesAsync(http, "tok", "sub-x", 200, CancellationToken.None));
    }

    [Fact]
    public async Task Varias_paginas_validas_se_concatenan()
    {
        var page1 = "{\"value\":[{\"name\":\"a\",\"properties\":{}}]," +
                    "\"nextLink\":\"https://management.azure.com/p?api-version=2025-01-01&$skiptoken=P2\"}";
        var page2 = "{\"value\":[{\"name\":\"b\",\"properties\":{}},{\"name\":\"c\",\"properties\":{}}]}";
        var http = new HttpClient(new RouteHandler(req =>
            req.RequestUri!.Query.Contains("skiptoken") ? Ok(page2) : Ok(page1)));

        var (items, truncated) = await AdvisorApiClient.ListAllPagesAsync(http, "tok", "sub-x", 200, CancellationToken.None);

        Assert.Equal(3, items.Count); // 1 + 2
        Assert.False(truncated); // todas las páginas dieron 200
    }
}
