namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// DTOs auxiliares de la pista Advisor Score (port de app/waf/advisor_score.py +
/// app/waf/advisor_api.py). Los records "forma de tabla" (WafAdvisorScoreSnapshot, AppJobLock)
/// y de proceso (AdvisorRow, AdvisorScoreData) viven en WafModels.cs y NO se redefinen aquí.
/// </summary>

/// <summary>
/// Grupo de suscripciones de una credencial de un cliente.
/// Equivale a la entrada {credential_id: {credential_name, subscriptions}} de
/// list_client_subscription_groups(). El dict de subscriptions es {subscription_id: subscription_name}.
/// </summary>
public sealed record WafSubscriptionGroup(
    int CredentialId,
    string CredentialName,
    IReadOnlyDictionary<string, string> Subscriptions);

/// <summary>Advertencia recopilada al puntuar (por credencial o por suscripción).</summary>
public sealed record WafScoreWarning(
    int? CredentialId,
    string? CredentialName,
    string? SubscriptionId,
    string? SubscriptionName,
    string Error);

/// <summary>Desglose del score de una suscripción: {pillar -> {score, weight}}.</summary>
public sealed record WafScoreBreakdown(
    string SubscriptionId,
    string SubscriptionName,
    IReadOnlyDictionary<string, AdvisorScoreData> Scores);

/// <summary>
/// Resultado del cálculo agregado de Advisor Score de un cliente (compute_advisor_score).
/// Pillars: {"1".."5" -> score 0-100 ponderado por consumo}. Status:
/// completed | partial | failed | no_connection.
/// </summary>
public sealed record WafAdvisorScoreResult(
    bool HasConnection,
    int SubscriptionsScored,
    IReadOnlyDictionary<string, decimal> Pillars,
    IReadOnlyList<WafScoreWarning> Warnings,
    IReadOnlyList<WafScoreBreakdown> Breakdown,
    string Status);

/// <summary>Una entrada del resumen de refresh masivo (refresh_all_advisor_scores).</summary>
public sealed record WafRefreshClientResult(
    int ClientId,
    string Status,
    string? SnapshotDate,
    string? CapturedAt,
    int? SubscriptionsScored,
    string? Error);

/// <summary>Resumen de refresh de todos los clientes activos (refresh_all_advisor_scores).</summary>
public sealed record WafRefreshAllResult(
    int ClientsTotal,
    int ClientsRefreshed,
    int ClientsFailed,
    IReadOnlyList<WafRefreshClientResult> Results);

/// <summary>
/// Métricas de una ingesta de recomendaciones desde la Advisor API
/// (advisor_api_items_to_rows + generate_and_list). Mismas claves que el dict Python.
/// </summary>
public sealed record WafIngestionMetrics(
    int RowsTotal,
    int RowsProcessed,
    int RowsSkipped,
    int RowsDuplicateSkipped,
    int AdvisorItems,
    string SubscriptionId,
    string SubscriptionName,
    bool PaginationTruncated = false);

/// <summary>Un punto histórico crudo de una suscripción para una serie (global o pilar), una granularidad.</summary>
public sealed record AdvisorHistoryPoint(DateOnly Date, decimal Score, decimal Weight);

/// <summary>
/// timeSeries de UNA suscripción para UN nivel de agregación. Series: clave 0 = global ("Advisor"),
/// 1..5 = pilar WAF. Cada valor es la lista de puntos por fecha.
/// </summary>
public sealed record SubscriptionScoreHistory(
    char Granularity, // 'D' | 'W' | 'M'
    IReadOnlyDictionary<int, IReadOnlyList<AdvisorHistoryPoint>> Series);

/// <summary>Punto agregado del cliente (una fecha). Series: 0=global, 1..5=pilar; solo claves con dato.</summary>
public sealed record ClientScoreHistoryPoint(
    DateOnly Date, IReadOnlyDictionary<int, decimal> Series);

/// <summary>Historial agregado del cliente para una granularidad; puntos ordenados por fecha asc.</summary>
public sealed record ClientScoreHistory(char Granularity, IReadOnlyList<ClientScoreHistoryPoint> Points);
