using System.Text.Json.Nodes;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Acceso a datos del Advisor Score. Port de las funciones persist/load/list de
/// app/waf/advisor_score.py. Schema lazy: cada método llama WafSchema.EnsureWafSchemaAsync.
/// SQL siempre parametrizado.
/// </summary>
public interface IAdvisorScoreStore
{
    /// <summary>IDs de clientes activos (clients con COALESCE(is_active,1)=1), ordenados por nombre.</summary>
    Task<IReadOnlyList<int>> ListActiveClientIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Grupos de suscripciones del cliente por credencial (solo subs is_active=1 y is_managed=1,
    /// con credencial activa). Equivale a list_client_subscription_groups().
    /// </summary>
    Task<IReadOnlyDictionary<int, WafSubscriptionGroup>> LoadClientSubscriptionGroupsAsync(
        int clientId, CancellationToken ct = default);

    /// <summary>
    /// Persiste el snapshot con MERGE idempotente sobre (client_id, snapshot_date, source) y
    /// devuelve el snapshot recién guardado (más reciente, con breakdown). Port de
    /// persist_advisor_score_snapshot. Si no se pasa snapshotDate usa hoy (UTC).
    /// </summary>
    Task<WafAdvisorScoreSnapshot?> PersistSnapshotAsync(
        int clientId, WafAdvisorScoreResult score, DateOnly? snapshotDate,
        string source, bool includeInReports, CancellationToken ct = default);

    /// <summary>
    /// Carga el snapshot más reciente del cliente (ORDER BY captured_at DESC, snapshot_date DESC).
    /// Port de load_latest_advisor_score_snapshot. null si no hay snapshot.
    /// </summary>
    Task<WafAdvisorScoreSnapshot?> LoadLatestSnapshotAsync(
        int clientId, bool includeBreakdown = false, CancellationToken ct = default);

    /// <summary>
    /// Mutex SQL distribuido (app_job_lock): adquiere si el lock está libre/expirado.
    /// Port de try_acquire_job_lock. Lease default 6h (21600s).
    /// </summary>
    Task<bool> TryAcquireJobLockAsync(
        string jobName, int leaseSeconds = 21600, CancellationToken ct = default);

    /// <summary>
    /// Historial del Advisor Score de los últimos 12 meses para el informe mensual (port de
    /// build_advisor_score_history). Devuelve { current, previous, delta, series } con un snapshot
    /// por mes (el más reciente con include_in_reports=1). Degrada a la forma vacía si la tabla no
    /// está disponible. El builder del informe lo embebe en 'advisor_score_history'.
    /// </summary>
    Task<JsonObject> BuildScoreHistoryAsync(int clientId, int year, int month, CancellationToken ct = default);

    /// <summary>
    /// Persiste (MERGE por client_id+granularity+point_date) el histórico agregado del cliente.
    /// Idempotente: re-refrescar actualiza los puntos existentes.
    /// </summary>
    Task PersistHistoryAsync(int clientId, IReadOnlyList<ClientScoreHistory> histories, CancellationToken ct = default);

    /// <summary>
    /// Carga los puntos del histórico del cliente para una granularidad ('D'/'W'/'M'), ordenados por
    /// fecha ascendente. Cada punto trae series 0=global y 1..5=pilar (solo claves no NULL).
    /// </summary>
    Task<IReadOnlyList<ClientScoreHistoryPoint>> LoadHistoryAsync(
        int clientId, char granularity, CancellationToken ct = default);
}
