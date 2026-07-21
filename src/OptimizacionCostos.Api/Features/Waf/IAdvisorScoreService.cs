namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Servicio de Advisor Score. Port de la lógica de orquestación de app/waf/advisor_score.py:
/// fetch por suscripción, cálculo agregado ponderado por consumo, refresh con persistencia,
/// refresh masivo, y generación/listado de recomendaciones.
/// </summary>
public interface IAdvisorScoreService
{
    /// <summary>
    /// Advisor Score oficial de una suscripción por pilar WAF: {pillar -> {score, weight}}.
    /// Delegación directa a IAdvisorApiClient.FetchSubscriptionScoreAsync.
    /// </summary>
    Task<IReadOnlyDictionary<int, AdvisorScoreData>> FetchSubscriptionScoreAsync(
        int credentialId, string subscriptionId, CancellationToken ct = default);

    /// <summary>
    /// Calcula el score agregado del cliente a partir de sus grupos de suscripciones.
    /// Promedio ponderado por consumo (weight) cuando hay peso; aritmético si no.
    /// Port de compute_advisor_score (carga grupos + fetch por suscripción).
    /// </summary>
    Task<WafAdvisorScoreResult> ComputeClientScoreAsync(
        int clientId, bool includeBreakdown, CancellationToken ct = default);

    /// <summary>
    /// Refresca y persiste el snapshot del cliente; devuelve el snapshot guardado (con breakdown).
    /// Port de refresh_client_advisor_score. Lanza si el cliente no existe.
    /// </summary>
    Task<WafAdvisorScoreSnapshot?> RefreshClientAsync(
        int clientId, DateOnly? snapshotDate, string source, bool includeInReports, CancellationToken ct = default);

    /// <summary>
    /// Refresca y persiste el histórico agregado del Advisor Score del cliente (timeSeries de Azure).
    /// Best-effort por suscripción: una que falle se registra y no aborta las demás.
    /// </summary>
    Task RefreshClientScoreHistoryAsync(int clientId, CancellationToken ct = default);

    /// <summary>
    /// Refresca los scores de varios clientes (o todos los activos si clientIds es null).
    /// Port de refresh_all_advisor_scores. Nunca lanza por un cliente: lo cuenta como fallido.
    /// </summary>
    Task<WafRefreshAllResult> RefreshAllAsync(
        IReadOnlyList<int>? clientIds, string source, bool includeInReports, CancellationToken ct = default);

    /// <summary>
    /// Genera y lista recomendaciones de Advisor para una suscripción (delegación a la API client).
    /// Port de generate_and_list_subscription_recommendations.
    /// </summary>
    Task<(IReadOnlyList<AdvisorRow> Rows, WafIngestionMetrics Metrics)> GenerateAndListRecommendationsAsync(
        int credentialId, string subscriptionId, string subscriptionName,
        int timeoutSeconds = 600, CancellationToken ct = default);

    /// <summary>
    /// Tipos de assessment de Defender vigentes en la suscripción (delegación a la API client).
    /// Null = cross-check no disponible (fail-open, no filtrar).
    /// </summary>
    Task<IReadOnlySet<string>?> FetchApplicableAssessmentTypesAsync(
        int credentialId, string subscriptionId, CancellationToken ct = default);
}
