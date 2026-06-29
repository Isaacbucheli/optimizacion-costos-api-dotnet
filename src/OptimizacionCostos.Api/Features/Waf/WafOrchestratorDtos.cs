namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Resultados del orquestador WAF (FASE 3). No son tablas: agregan lo que devuelven las rutas
/// FastAPI que componen varios servicios (advisor-sync, consolidate-duplicates, analyze-all).
/// Mantienen las mismas claves snake_case del FastAPI cuando el serializador global las aplica.
/// </summary>

/// <summary>
/// Resultado del sync de Azure Advisor (port del dict que arma _run_advisor_sync_job +
/// la respuesta de start_waf_advisor_sync). Incluye el resumen de la ingesta, la curación IA
/// posterior y la consolidación final.
/// </summary>
public sealed record WafAdvisorSyncResult(
    int RunId,
    string Status,
    int SubscriptionsQueued,
    int SubscriptionsProcessed,
    int SubscriptionsFailed,
    int NewRecommendations,
    int NewFindings,
    int ResolvedFindings,
    int MergedDuplicates,
    int AiProcessed,
    int AiErrors,
    IReadOnlyList<object> Warnings);

/// <summary>
/// Resultado de la consolidación manual de duplicados (port de _consolidate_client_duplicates).
/// </summary>
public sealed record WafConsolidationResult(
    int Merged,
    int AiCalls);

/// <summary>
/// Resultado de la curación IA por lotes de un cliente (port de _auto_curate_client_recommendations).
/// <see cref="Skipped"/> = "not_configured" cuando Azure OpenAI no está configurado (null si corrió).
/// </summary>
public sealed record WafBatchCurationResult(
    int Processed,
    int Errors,
    string? Skipped = null);
