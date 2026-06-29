using System.Text;
using System.Text.Json;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Cliente HTTP de Azure OpenAI chat completions. Port de _post_chat (Python).
/// Síncrono porque el motor de costos es síncrono. Devuelve el content del assistant
/// (texto JSON esperado) o null si la llamada falla.
/// </summary>
public sealed class AzureOpenAiChatClient(HttpClient http, AppConfig config) : IChatCompletionClient
{
    public string? Complete(string systemPrompt, string userJson, int maxCompletionTokens = 500)
    {
        var endpoint = config.AzureOpenAiEndpoint.TrimEnd('/');
        var url = $"{endpoint}/openai/deployments/{config.AzureOpenAiDeployment}/chat/completions"
                + $"?api-version={config.AzureOpenAiApiVersion}";

        var payload = new
        {
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userJson },
            },
            max_completion_tokens = maxCompletionTokens,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("api-key", config.AzureOpenAiApiKey);

        using var resp = http.Send(req);
        resp.EnsureSuccessStatusCode();
        var body = resp.Content.ReadAsStream();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
    }
}
