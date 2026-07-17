using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using OptimizacionCostos.Api.Features.Waf;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Waf;

public sealed class WafTranslationServiceTests
{
    private sealed class FakeChat : IChatCompletionClient
    {
        public string? Response { get; set; }
        public string? LastUser { get; private set; }
        public int Calls { get; private set; }
        public string? Complete(string systemPrompt, string userJson, int maxCompletionTokens = 500)
        {
            Calls++;
            LastUser = userJson;
            return Response;
        }
    }

    private static AppConfig Configured() => new()
    {
        AzureOpenAiEndpoint = "https://x.openai.azure.com",
        AzureOpenAiApiKey = "k",
        AzureOpenAiDeployment = "gpt",
        AzureOpenAiApiVersion = "2025-04-01-preview",
    };

    [Fact]
    public void IsConfigured_FalseCuandoFaltanClaves()
    {
        var svc = new WafTranslationService(new FakeChat(), new AppConfig());
        Assert.False(svc.IsConfigured);
    }

    [Fact]
    public void IsConfigured_TrueConTodasLasClaves()
    {
        var svc = new WafTranslationService(new FakeChat(), Configured());
        Assert.True(svc.IsConfigured);
    }

    [Fact]
    public async Task TranslateAsync_DeduplicaYMapeaPorClave_YRespetaVacios()
    {
        // Dos ítems con el mismo texto "Hola" + uno vacío: el modelo solo ve el único no vacío.
        var chat = new FakeChat { Response = "[\"Hello\"]" };
        var svc = new WafTranslationService(chat, Configured());

        var items = new List<WafTranslationItem>
        {
            new("a", "Hola"), new("b", "Hola"), new("c", "   "),
        };
        var result = await svc.TranslateAsync("en", items, default);

        Assert.Equal(1, chat.Calls); // una sola llamada, con el texto único
        var map = result.ToDictionary(x => x.Key, x => x.Text);
        Assert.Equal("Hello", map["a"]);
        Assert.Equal("Hello", map["b"]);
        Assert.Equal("   ", map["c"]); // vacío/espacios pasa sin cambio
    }

    [Fact]
    public async Task TranslateAsync_TargetNoEn_Lanza()
    {
        var svc = new WafTranslationService(new FakeChat { Response = "[]" }, Configured());
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.TranslateAsync("fr", new List<WafTranslationItem> { new("a", "Hola") }, default));
    }
}
