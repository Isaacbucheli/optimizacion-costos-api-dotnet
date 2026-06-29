namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Curador IA de recomendaciones WAF (port de app/waf/ai_curator.py). Traduce/mejora los textos
/// al español, decide include/exclude/review, detecta costo adicional y puede reasignar el pilar.
/// </summary>
public interface IWafCuratorService
{
    /// <summary>
    /// Envía una recomendación canónica a Azure OpenAI y devuelve el resultado curado, normalizado
    /// (decisión válida, pilar 1-5, confidence [0,1], textos limpios y truncados, raw 4000 chars).
    /// Port de analyze_recommendation_with_ai: 4 intentos con backoff (429/503), timeout alto.
    /// Lanza si Azure OpenAI no está configurado o si la llamada falla definitivamente (el batch
    /// llamador captura y continúa).
    /// </summary>
    Task<WafCurationResult> AnalyzeAsync(WafCanonical recommendation, CancellationToken ct = default);

    /// <summary>
    /// Estado de configuración de Azure OpenAI (sin exponer la clave). Port de
    /// azure_openai_config_status(): configured = endpoint+key+deployment+api_version presentes.
    /// </summary>
    (bool Configured, string Deployment, string ApiVersion, bool HasKey) GetConfigStatus();
}
