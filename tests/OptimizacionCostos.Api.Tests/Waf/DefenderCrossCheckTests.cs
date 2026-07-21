using System.Net;
using System.Text.Json;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Cross-check Defender (spec 2026-07-21): el portal de Advisor oculta las recomendaciones de
/// Seguridad cuyo assessment de Defender está NotApplicable o ya no existe (mantiene Unhealthy y
/// Healthy). El sync replica esa regla: set de tipos "vigentes" + filtro fail-open.
/// </summary>
public sealed class DefenderCrossCheckTests
{
    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(route(request));
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static HttpResponseMessage Forbidden() =>
        new(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"code\":\"AuthorizationFailed\"}}") };
    private static HttpResponseMessage NotFound() =>
        new(HttpStatusCode.NotFound) { Content = new StringContent("gone") };

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static AdvisorRow Row(string category, string? typeId, string name = "rec") => new(
        AdvisorName: name, AdvisorCategory: category, BusinessImpact: "Medium", ResourceName: "res",
        ResourceType: "t", ResourceGroup: "rg", SubscriptionId: "sub-1", SubscriptionName: "Sub",
        AzureResourceId: "/subscriptions/sub-1/x", AdditionalInfo: null, RecommendationTypeId: typeId);

    // ------------------------- ParseApplicableAssessments -------------------------

    [Fact]
    public void Parse_incluye_unhealthy_y_healthy_excluye_notapplicable()
    {
        var root = Parse("""
            {"value":[
              {"name":"AAA","properties":{"status":{"code":"Unhealthy"}}},
              {"name":"BBB","properties":{"status":{"code":"Healthy"}}},
              {"name":"CCC","properties":{"status":{"code":"NotApplicable"}}},
              {"name":"DDD","properties":{"status":{"code":"notapplicable"}}},
              {"name":"EEE","properties":{"status":{"code":"SomethingNew"}}},
              {"name":"FFF","properties":{}},
              {"name":"GGG"}
            ]}
            """);

        var set = AdvisorApiClient.ParseApplicableAssessments(root);

        Assert.Equal(new HashSet<string> { "aaa", "bbb", "eee" }, set);
    }

    [Fact]
    public void Parse_mismo_tipo_na_y_unhealthy_queda_vigente()
    {
        // Un tipo con varios recursos: basta UN assessment no-NA para considerarlo vigente.
        var root = Parse("""
            {"value":[
              {"name":"AAA","properties":{"status":{"code":"NotApplicable"}}},
              {"name":"AAA","properties":{"status":{"code":"Unhealthy"}}}
            ]}
            """);

        Assert.Contains("aaa", AdvisorApiClient.ParseApplicableAssessments(root));
    }

    // ------------------------- FilterSecurityRowsByDefender -------------------------

    [Fact]
    public void Filtro_descarta_security_fuera_del_set_y_conserva_el_resto()
    {
        var rows = new[]
        {
            Row("Security", "aaa"),           // vigente -> queda
            Row("Security", "zzz"),           // NO vigente -> se descarta
            Row("Security", null),            // sin typeId -> queda (conservador)
            Row("HighAvailability", "zzz"),   // otra categoría -> queda siempre
        };
        var set = new HashSet<string> { "aaa" };

        var (kept, dropped) = AdvisorApiClient.FilterSecurityRowsByDefender(rows, set);

        Assert.Equal(3, kept.Count);
        Assert.Equal(1, dropped);
        Assert.DoesNotContain(kept, r => r.AdvisorCategory == "Security" && r.RecommendationTypeId == "zzz");
    }

    [Fact]
    public void Filtro_con_set_null_o_vacio_no_filtra_nada()
    {
        var rows = new[] { Row("Security", "zzz") };

        var (keptNull, droppedNull) = AdvisorApiClient.FilterSecurityRowsByDefender(rows, null);
        var (keptEmpty, droppedEmpty) = AdvisorApiClient.FilterSecurityRowsByDefender(rows, new HashSet<string>());

        Assert.Single(keptNull);
        Assert.Equal(0, droppedNull);
        Assert.Single(keptEmpty);
        Assert.Equal(0, droppedEmpty);
    }

    // ------------------------- ApplyDefenderCrossCheck (orquestador) -------------------------

    [Fact]
    public void CrossCheck_set_null_reporta_unavailable_y_no_filtra()
    {
        var rows = new[] { Row("Security", "zzz") };

        var (kept, check, skipped) = WafSyncOrchestrator.ApplyDefenderCrossCheck(rows, null);

        Assert.Single(kept);
        Assert.Equal("unavailable", check);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void CrossCheck_set_valido_reporta_ok_y_cuenta_descartadas()
    {
        var rows = new[] { Row("Security", "aaa"), Row("Security", "zzz") };

        var (kept, check, skipped) = WafSyncOrchestrator.ApplyDefenderCrossCheck(
            rows, new HashSet<string> { "aaa" });

        Assert.Single(kept);
        Assert.Equal("ok", check);
        Assert.Equal(1, skipped);
    }

    // ------------------------- ListApplicableAssessmentTypesAsync -------------------------

    [Fact]
    public async Task Lista_paginada_ok_une_las_paginas()
    {
        var page1 = "{\"value\":[{\"name\":\"AAA\",\"properties\":{\"status\":{\"code\":\"Unhealthy\"}}}]," +
                    "\"nextLink\":\"https://management.azure.com/p?api-version=2021-06-01&$skipToken=P2\"}";
        var page2 = "{\"value\":[{\"name\":\"BBB\",\"properties\":{\"status\":{\"code\":\"Healthy\"}}}]}";
        var http = new HttpClient(new RouteHandler(req =>
            req.RequestUri!.Query.Contains("skipToken") ? Ok(page2) : Ok(page1)));

        var set = await AdvisorApiClient.ListApplicableAssessmentTypesAsync(http, "tok", "sub-x", CancellationToken.None);

        Assert.NotNull(set);
        Assert.Equal(new HashSet<string> { "aaa", "bbb" }, set);
    }

    [Fact]
    public async Task Primera_pagina_403_devuelve_null_fail_open()
    {
        var http = new HttpClient(new RouteHandler(_ => Forbidden()));

        var set = await AdvisorApiClient.ListApplicableAssessmentTypesAsync(http, "tok", "sub-x", CancellationToken.None);

        Assert.Null(set);
    }

    [Fact]
    public async Task Pagina_de_continuacion_rota_devuelve_null_set_incompleto_no_filtra()
    {
        var page1 = "{\"value\":[{\"name\":\"AAA\",\"properties\":{\"status\":{\"code\":\"Unhealthy\"}}}]," +
                    "\"nextLink\":\"https://management.azure.com/p?api-version=2021-06-01&$skipToken=P2\"}";
        var http = new HttpClient(new RouteHandler(req =>
            req.RequestUri!.Query.Contains("skipToken") ? NotFound() : Ok(page1)));

        var set = await AdvisorApiClient.ListApplicableAssessmentTypesAsync(http, "tok", "sub-x", CancellationToken.None);

        Assert.Null(set);
    }
}
