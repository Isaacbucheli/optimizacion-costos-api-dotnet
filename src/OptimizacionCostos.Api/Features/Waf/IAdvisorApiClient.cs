namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Cliente REST de la Azure Advisor API (ARM). Port de app/waf/advisor_api.py.
/// Usa IAzureCredentialFactory (token ARM por credencial) + IHttpClientFactory.
/// Dos responsabilidades:
/// 1) Advisor Score oficial por suscripción (api-version 2023-01-01) -> por pilar WAF.
/// 2) Generar y listar recomendaciones de Advisor (api-version 2025-01-01) -> AdvisorRow[].
/// </summary>
public interface IAdvisorApiClient
{
    /// <summary>
    /// Devuelve el Advisor Score oficial de Azure por pilar WAF para una suscripción:
    /// {pillar(1-5) -> {score 0-100, weight=consumptionUnits}}. Solo categorías mapeables.
    /// Lanza AdvisorApiException si el HTTP no es 200.
    /// </summary>
    Task<IReadOnlyDictionary<int, AdvisorScoreData>> FetchSubscriptionScoreAsync(
        int credentialId, string subscriptionId, CancellationToken ct = default);

    /// <summary>
    /// Devuelve el histórico (timeSeries) del Advisor Score de una suscripción por nivel de agregación
    /// (D/W/M), con series 0=global y 1..5=pilar. Mismo GET que FetchSubscriptionScoreAsync.
    /// </summary>
    Task<IReadOnlyList<SubscriptionScoreHistory>> FetchSubscriptionScoreHistoryAsync(
        int credentialId, string subscriptionId, CancellationToken ct = default);

    /// <summary>
    /// Dispara la generación de recomendaciones (POST generateRecommendations -> 202 + Location),
    /// hace polling hasta 204 y luego lista las recomendaciones paginadas, devolviendo filas
    /// deduplicadas más métricas. Port de generate_and_list_subscription_recommendations.
    /// </summary>
    Task<(IReadOnlyList<AdvisorRow> Rows, WafIngestionMetrics Metrics)> GenerateAndListRecommendationsAsync(
        int credentialId, string subscriptionId, string subscriptionName,
        int timeoutSeconds = 600, int pageSize = 200, CancellationToken ct = default);
}

/// <summary>Se lanza cuando Azure Advisor no puede generar/retornar datos. Port de AdvisorApiError.</summary>
public sealed class AdvisorApiException(string message) : Exception(message);
