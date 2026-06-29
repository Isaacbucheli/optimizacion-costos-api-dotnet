using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Pruebas del asistente de precios IA (lógica de selección), sin red: el chat de Azure OpenAI
/// se inyecta como fake que devuelve un JSON canned. Cubre: elige por índice con confianza ≥0.6
/// y marca la fila; null por confianza baja / índice null / índice fuera de rango / deshabilitado
/// / sin candidatos / respuesta no-JSON.
/// </summary>
public class PriceAssistantTests
{
    private sealed class FakeChat(string? resp) : IChatCompletionClient
    {
        public string? Complete(string systemPrompt, string userJson, int maxCompletionTokens = 500) => resp;
    }

    private static AppConfig Cfg(bool enabled = true, string key = "k", string mode = "assist_match") => new()
    {
        AzureOpenAiEnabled = enabled,
        AzureOpenAiEndpoint = "https://x.openai.azure.com/",
        AzureOpenAiApiKey = key,
        AzureOpenAiDeployment = "d",
        AzureOpenAiApiVersion = "2025-04-01-preview",
        PricingAssistMode = mode,
    };

    private static SqlPriceAssistant Make(string? resp, AppConfig? cfg = null) =>
        new(cfg ?? Cfg(), new FakeChat(resp), new NoopPriceAssistantAudit());

    private static List<PriceRow> Candidates() =>
    [
        new PriceRow { PriceId = 10, ProductName = "General Purpose Compute Gen5", MeterName = "vCore", RetailPrice = 0.5 },
        new PriceRow { PriceId = 11, ProductName = "Hyperscale - Compute Gen5", MeterName = "vCore", RetailPrice = 0.18266 },
    ];

    private static readonly Dictionary<string, object?> Ctx = new() { ["sku"] = "HS_Gen5", ["region"] = "eastus2" };

    [Fact]
    public void Elige_candidato_con_confianza_ok_y_lo_marca()
    {
        var a = Make("{\"candidate_index\":1,\"confidence\":0.9,\"reason_es\":\"hyperscale gen5\"}");
        var r = a.SelectCandidate("sql", "compute", Ctx, Candidates());
        Assert.NotNull(r);
        Assert.Equal(11L, r!.PriceId);
        Assert.Equal(0.18266, r.RetailPrice);
        Assert.Equal("assist_match:compute", r.AiMatchStrategy);
        Assert.Equal(0.9, r.AiMatchConfidence);
        Assert.Equal("hyperscale gen5", r.AiMatchReason);
    }

    [Fact]
    public void Null_si_confianza_bajo_umbral()
    {
        var a = Make("{\"candidate_index\":1,\"confidence\":0.5}");
        Assert.Null(a.SelectCandidate("sql", "compute", Ctx, Candidates()));
    }

    [Fact]
    public void Null_si_indice_null()
    {
        var a = Make("{\"candidate_index\":null,\"confidence\":0.95}");
        Assert.Null(a.SelectCandidate("sql", "compute", Ctx, Candidates()));
    }

    [Fact]
    public void Null_si_indice_fuera_de_rango()
    {
        var a = Make("{\"candidate_index\":7,\"confidence\":0.95}");
        Assert.Null(a.SelectCandidate("sql", "compute", Ctx, Candidates()));
    }

    [Fact]
    public void Null_si_deshabilitado()
    {
        var a = Make("{\"candidate_index\":1,\"confidence\":0.95}", Cfg(enabled: false));
        Assert.Null(a.SelectCandidate("sql", "compute", Ctx, Candidates()));
    }

    [Fact]
    public void Null_si_sin_candidatos()
    {
        var a = Make("{\"candidate_index\":0,\"confidence\":0.95}");
        Assert.Null(a.SelectCandidate("sql", "compute", Ctx, []));
    }

    [Fact]
    public void Null_si_respuesta_no_es_json()
    {
        var a = Make("lo siento, no encontré un candidato");
        Assert.Null(a.SelectCandidate("sql", "compute", Ctx, Candidates()));
    }

    [Fact]
    public void IsEnabled_falso_si_falta_la_clave()
    {
        Assert.False(Make(null, Cfg(key: "")).IsEnabled);
    }

    [Fact]
    public void IsEnabled_falso_si_modo_distinto()
    {
        Assert.False(Make(null, Cfg(mode: "off")).IsEnabled);
    }
}
