using System.Text.Json;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Traducción es→en reusando IChatCompletionClient (Azure OpenAI). Sin estado ni persistencia.
/// Deduplica por texto y hace UNA llamada por lote. NO loguea textos ni claves.
/// </summary>
public sealed class WafTranslationService(IChatCompletionClient chat, AppConfig config) : IWafTranslationService
{
    public bool IsConfigured =>
        (config.AzureOpenAiEndpoint ?? "").Trim().Length > 0
        && (config.AzureOpenAiApiKey ?? "").Trim().Length > 0
        && (config.AzureOpenAiDeployment ?? "").Trim().Length > 0
        && (config.AzureOpenAiApiVersion ?? "").Trim().Length > 0;

    public async Task<IReadOnlyList<WafTranslationItem>> TranslateAsync(
        string target, IReadOnlyList<WafTranslationItem> items, CancellationToken ct = default)
    {
        if (!string.Equals(target, "en", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Solo se admite target=en.", nameof(target));

        // Textos únicos no vacíos → una sola llamada.
        var unique = items
            .Select(i => i.Text ?? "")
            .Where(t => t.Trim().Length > 0)
            .Distinct()
            .ToList();

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (unique.Count > 0)
        {
            var userJson = JsonSerializer.Serialize(unique);
            var maxTokens = Math.Clamp(unique.Sum(t => t.Length) + 512, 512, 8000);
            var translated = await TranslateBatchAsync(userJson, unique.Count, maxTokens, ct);
            for (var i = 0; i < unique.Count; i++)
                map[unique[i]] = translated[i];
        }

        // Re-expande a los ítems originales por clave; vacíos/espacios sin cambio.
        return items
            .Select(i => new WafTranslationItem(i.Key, map.TryGetValue(i.Text ?? "", out var en) ? en : (i.Text ?? "")))
            .ToList();
    }

    // 3 intentos; el IChatCompletionClient esconde la capa HTTP, así que se reintenta ante cualquier fallo.
    private async Task<string[]> TranslateBatchAsync(string userJson, int expected, int maxTokens, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var raw = await Task.Run(() => chat.Complete(WafPrompts.TranslateEsToEnSystem, userJson, maxTokens), ct);
                if (string.IsNullOrWhiteSpace(raw))
                    throw new InvalidOperationException("respuesta de traduccion vacia");
                var arr = ParseStringArray(raw!);
                if (arr.Length != expected)
                    throw new InvalidOperationException($"la traduccion devolvio {arr.Length} de {expected} elementos");
                return arr;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                if (attempt >= 2) break;
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct); }
                catch (OperationCanceledException) { throw; }
            }
        }
        throw new InvalidOperationException("No se pudo obtener la traduccion de Azure OpenAI.", last);
    }

    // Extrae un arreglo JSON de cadenas, tolerando fences ```json y texto alrededor.
    private static string[] ParseStringArray(string raw)
    {
        var s = raw.Trim();
        var start = s.IndexOf('[');
        var end = s.LastIndexOf(']');
        if (start >= 0 && end > start) s = s.Substring(start, end - start + 1);
        return JsonSerializer.Deserialize<string[]>(s) ?? Array.Empty<string>();
    }
}
