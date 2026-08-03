using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.Boletin;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Tests.Boletin;

public class BoletinTranslationServiceTests
{
    private sealed class FakeChat : IChatCompletionClient
    {
        public List<(string System, string User)> Calls { get; } = [];
        public Queue<string?> Responses { get; } = new();
        public string? Complete(string systemPrompt, string userJson, int maxCompletionTokens = 500)
        {
            Calls.Add((systemPrompt, userJson));
            return Responses.Count > 0 ? Responses.Dequeue() : null;
        }
    }

    private static AppConfig Configured() => new()
    {
        AzureOpenAiEndpoint = "https://x.openai.azure.com",
        AzureOpenAiApiKey = "k",
        AzureOpenAiDeployment = "d",
        AzureOpenAiApiVersion = "2025-04-01-preview",
    };

    private static ILogger<BoletinTranslationService> Logger() => NullLogger<BoletinTranslationService>.Instance;

    [Fact]
    public void SinConfiguracionNoEstaConfigurado()
    {
        var svc = new BoletinTranslationService(new FakeChat(), new AppConfig(), Logger());
        Assert.False(svc.IsConfigured);
    }

    [Fact]
    public async Task TraduceYDeduplicaPorTexto()
    {
        var chat = new FakeChat();
        // 2 textos únicos aunque llegan 3 items → una llamada, array de 2.
        chat.Responses.Enqueue("""["Soporte de Node.js 20 termina", "Actualiza tus apps"]""");
        var svc = new BoletinTranslationService(chat, Configured(), Logger());

        var result = await svc.TranslateToSpanishAsync(
        [
            new BoletinTranslationItem("a", "Support for Node.js 20 ends"),
            new BoletinTranslationItem("b", "Upgrade your apps"),
            new BoletinTranslationItem("c", "Support for Node.js 20 ends"),
        ]);

        Assert.Single(chat.Calls);
        Assert.Equal(3, result.Count);
        Assert.Equal("Soporte de Node.js 20 termina", result.Single(r => r.Key == "a").Text);
        Assert.Equal("Soporte de Node.js 20 termina", result.Single(r => r.Key == "c").Text);
        Assert.Equal("Actualiza tus apps", result.Single(r => r.Key == "b").Text);
        Assert.Contains("español", chat.Calls[0].System, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToleraFencesDeMarkdown()
    {
        var chat = new FakeChat();
        chat.Responses.Enqueue("```json\n[\"Hola\"]\n```");
        var svc = new BoletinTranslationService(chat, Configured(), Logger());
        var result = await svc.TranslateToSpanishAsync([new BoletinTranslationItem("k", "Hello")]);
        Assert.Equal("Hola", result[0].Text);
    }

    [Fact]
    public async Task LongitudIncorrectaReintentaYLuegoFalla()
    {
        var chat = new FakeChat();
        chat.Responses.Enqueue("""["uno", "dos"]"""); // esperaba 1
        chat.Responses.Enqueue("""["uno", "dos"]""");
        chat.Responses.Enqueue("""["uno", "dos"]""");
        var svc = new BoletinTranslationService(chat, Configured(), Logger());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.TranslateToSpanishAsync([new BoletinTranslationItem("k", "one")]));
        Assert.Equal(3, chat.Calls.Count); // 3 intentos
    }

    [Fact]
    public async Task ListaVaciaNoLlamaALaIa()
    {
        var chat = new FakeChat();
        var svc = new BoletinTranslationService(chat, Configured(), Logger());
        var result = await svc.TranslateToSpanishAsync([]);
        Assert.Empty(result);
        Assert.Empty(chat.Calls);
    }
}
