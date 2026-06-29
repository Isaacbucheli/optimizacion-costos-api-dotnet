namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Asistente de precios con IA (Azure OpenAI). Fallback cuando la selección determinista
/// no encuentra precio: elige el MEJOR candidato REAL de una lista del Retail API — NUNCA
/// inventa precios/SKUs/medidores. Port de app/pricing/openai_price_assistant.py. Gateado
/// por config (AzureOpenAiEnabled + claves + PricingAssistMode); cada llamada se audita.
/// </summary>
public interface IPriceAssistant
{
    bool IsEnabled { get; }

    /// <summary>
    /// Candidato elegido por la IA (marcado con AiMatch*) si la confianza ≥ umbral y el índice
    /// es válido; null si no hay candidatos, está deshabilitado, o la IA no eligió con confianza.
    /// </summary>
    PriceRow? SelectCandidate(
        string serviceKey,
        string component,
        IReadOnlyDictionary<string, object?> context,
        IReadOnlyList<PriceRow> candidates);
}

/// <summary>Seam del chat de Azure OpenAI (aislado para testear la lógica sin red).</summary>
public interface IChatCompletionClient
{
    /// <summary>
    /// Devuelve el contenido (texto, se espera JSON) del mensaje del assistant, o null si falla.
    /// <paramref name="maxCompletionTokens"/> acota la respuesta: el motor de costos usa el default
    /// (respuestas cortas), pero la narrativa del informe necesita un límite mayor (resúmenes largos).
    /// </summary>
    string? Complete(string systemPrompt, string userJson, int maxCompletionTokens = 500);
}

/// <summary>Auditoría de cada invocación del asistente → dbo.price_assistant_audit.</summary>
public interface IPriceAssistantAudit
{
    void Record(PriceAssistantAuditEntry entry);
}

public sealed record PriceAssistantAuditEntry(
    string ServiceKey,
    string Component,
    string ContextJson,
    int CandidateCount,
    long? SelectedPriceId,
    int? SelectedIndex,
    double? Confidence,
    bool Accepted,
    string? ReasonEs,
    string? RawResponse);

/// <summary>Auditoría no-op (tests / cuando no interesa persistir).</summary>
public sealed class NoopPriceAssistantAudit : IPriceAssistantAudit
{
    public void Record(PriceAssistantAuditEntry entry) { }
}
