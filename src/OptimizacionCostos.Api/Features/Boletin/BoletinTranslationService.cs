using System.Text.Json;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.Boletin;

public sealed record BoletinTranslationItem(string Key, string Text);

public interface IBoletinTranslationService
{
    /// <summary>true si Azure OpenAI tiene endpoint+key+deployment+apiVersion (mismo criterio que WAF).</summary>
    bool IsConfigured { get; }

    /// <summary>Traduce en→es en lote (dedup por texto). Lanza InvalidOperationException si la IA
    /// responde mal tras los reintentos. NO persiste nada.</summary>
    Task<IReadOnlyList<BoletinTranslationItem>> TranslateToSpanishAsync(
        IReadOnlyList<BoletinTranslationItem> items, CancellationToken ct = default);
}

/// <summary>Espejo en→es de Features/Waf/WafTranslationService (mismo IChatCompletionClient,
/// mismo chunking y reintentos), con prompt de FIDELIDAD (el boletín no re-redacta a Microsoft).</summary>
public sealed class BoletinTranslationService(
    IChatCompletionClient chat, AppConfig config, ILogger<BoletinTranslationService> logger) : IBoletinTranslationService
{
    private const int MaxItemsPerChunk = 100;
    private const int MaxCharsPerChunk = 8000;
    private const int MaxAttempts = 3;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(config.AzureOpenAiEndpoint)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiKey)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiDeployment)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiVersion);

    public async Task<IReadOnlyList<BoletinTranslationItem>> TranslateToSpanishAsync(
        IReadOnlyList<BoletinTranslationItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return [];

        var unique = items.Select(i => i.Text).Distinct(StringComparer.Ordinal).ToList();
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var chunk in Chunk(unique))
        {
            ct.ThrowIfCancellationRequested();
            var translated = await TranslateBatchAsync(chunk, ct);
            for (var i = 0; i < chunk.Count; i++) translations[chunk[i]] = translated[i];
        }

        return items.Select(i => i with { Text = translations[i.Text] }).ToList();
    }

    private static List<List<string>> Chunk(List<string> texts)
    {
        var chunks = new List<List<string>>();
        var current = new List<string>();
        var chars = 0;
        foreach (var t in texts)
        {
            if (current.Count > 0 && (current.Count >= MaxItemsPerChunk || chars + t.Length > MaxCharsPerChunk))
            {
                chunks.Add(current);
                current = [];
                chars = 0;
            }
            current.Add(t);
            chars += t.Length;
        }
        if (current.Count > 0) chunks.Add(current);
        return chunks;
    }

    private async Task<List<string>> TranslateBatchAsync(List<string> chunk, CancellationToken ct)
    {
        var userJson = JsonSerializer.Serialize(chunk);
        var maxTokens = Math.Clamp(chunk.Sum(t => t.Length) * 2 + 512, 512, 8000);

        // Diagnóstico de fallos (Item 3 del review): antes de este cambio, agotar los reintentos
        // lanzaba InvalidOperationException SIN la causa original — imposible saber, desde los logs,
        // si falló por HTTP/auth/timeout de Azure OpenAI o porque la IA respondió con longitud/JSON
        // incorrecto. `last` guarda la última excepción real (queda null si el fallo fue de
        // longitud/parse, no de excepción) y cada intento fallido se loguea de inmediato.
        Exception? last = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // El seam IChatCompletionClient es síncrono a propósito (motor de costos); Task.Run como el WAF.
                var raw = await Task.Run(() => chat.Complete(BoletinPrompts.TranslateEnToEsSystem, userJson, maxTokens), ct);
                var parsed = ParseStringArray(raw);
                if (parsed is not null && parsed.Count == chunk.Count) return parsed;
                logger.LogWarning(
                    "traduccion boletin: respuesta invalida en intento {Attempt} de {Max} (longitud/parse incorrecto)",
                    attempt + 1, MaxAttempts);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                logger.LogWarning(ex, "traduccion boletin: intento {Attempt} de {Max} fallo", attempt + 1, MaxAttempts);
            }
            if (attempt < MaxAttempts - 1)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), ct);
        }
        throw new InvalidOperationException("No se pudo obtener la traduccion de Azure OpenAI.", last);
    }

    /// <summary>Tolera fences ```json ...``` recortando entre el primer '[' y el último ']'.</summary>
    internal static List<string>? ParseStringArray(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(raw[start..(end + 1)]);
            return arr;
        }
        catch (JsonException) { return null; }
    }
}
