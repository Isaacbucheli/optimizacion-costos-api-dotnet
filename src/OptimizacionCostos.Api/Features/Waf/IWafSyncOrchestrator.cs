namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// FASE 3 — Orquestador del módulo WAF. Compone los servicios/stores existentes para reproducir
/// los flujos multi-paso que en el FastAPI vivían inline en app/routes/waf.py
/// (ingest_advisor_csv, _run_advisor_sync_job, _consolidate_client_duplicates,
/// _auto_curate_client_recommendations). Los controllers se quedan finos: validan acceso/rol,
/// llaman aquí y serializan el resultado.
/// </summary>
public interface IWafSyncOrchestrator
{
    /// <summary>
    /// Ingesta de un CSV de Azure Advisor (carga manual síncrona). Port de ingest_advisor_csv /
    /// upload_waf_ingestion: construye el resolver de dedup (tope IA CSV), parsea el CSV e ingiere
    /// las filas (sin replaceSubscriptionIds → no resuelve hallazgos previos). Devuelve el resultado
    /// de la corrida y cuántos duplicados fusionó el resolver (merged_duplicates).
    /// </summary>
    Task<(WafIngestionResult Result, int Merged)> RunCsvAsync(
        int clientId, string fileName, byte[] content, string? createdBy, CancellationToken ct = default);

    /// <summary>
    /// Sincronización con la Azure Advisor API. Port de _run_advisor_sync_job: carga los grupos de
    /// suscripciones gestionadas, genera/lista recomendaciones por suscripción (acumulando warnings
    /// por suscripción/credencial fallida), ingiere con replaceSubscriptionIds = subs exitosas,
    /// cura por IA las nuevas canónicas y consolida los duplicados resultantes. Devuelve el resumen.
    /// </summary>
    Task<WafAdvisorSyncResult> RunAdvisorSyncAsync(
        int clientId, IReadOnlyList<string>? subscriptions, string? createdBy,
        int timeoutSecondsPerSubscription,
        Func<WafAdvisorSyncProgress, CancellationToken, Task>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Consolida duplicados ya curados del cliente (titulos ES casi idénticos). Port de
    /// _consolidate_client_duplicates: abre conn+tx, carga candidatos, planifica (con confirmación
    /// IA opcional), aplica fusiones, recalcula counts + renumera, commit. Devuelve cuántos fusionó.
    /// </summary>
    Task<WafConsolidationResult> ConsolidateAsync(
        int clientId, bool useAi, string? actor, CancellationToken ct = default);

    /// <summary>
    /// Curación IA por lotes de las canónicas pendientes del cliente. Port de
    /// _auto_curate_client_recommendations: lista pendientes (priorizando las recién creadas),
    /// analiza cada una con el curador IA, persiste la sugerencia y renumera. Tolerante a fallos por
    /// canónica (los cuenta como errores). Devuelve el resumen.
    /// </summary>
    Task<WafBatchCurationResult> CurateClientAsync(
        int clientId, int limit, IReadOnlyList<int>? preferredCanonicalIds, string? actor, CancellationToken ct = default);
}
