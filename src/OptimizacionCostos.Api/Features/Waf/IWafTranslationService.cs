namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>Ítem de traducción (clave estable del llamador + texto). Proceso, no tabla.</summary>
public sealed record WafTranslationItem(string Key, string Text);

/// <summary>
/// Traducción bajo demanda del contenido WAF (es→en). Sin estado: no persiste nada.
/// Reusa el IChatCompletionClient existente (Azure OpenAI).
/// </summary>
public interface IWafTranslationService
{
    /// <summary>True si Azure OpenAI está configurado (mismo criterio que el curador).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Traduce los textos al idioma destino ("en"). Deduplica por texto, respeta orden por clave
    /// y devuelve los vacíos/espacios sin cambio. Lanza ArgumentException si target != "en".
    /// </summary>
    Task<IReadOnlyList<WafTranslationItem>> TranslateAsync(
        string target, IReadOnlyList<WafTranslationItem> items, CancellationToken ct = default);
}
