using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Ingesta de recomendaciones de Azure Advisor (CSV o filas de la API) a la matriz del cliente.
/// Port de app/waf/ingestion.py (parse_advisor_csv, ingest_advisor_rows, mark_stale_runs).
/// Reusa IWafCatalogStore (canónicas/alias) e IWafRecommendationStore (recalcular/renumerar).
/// </summary>
public interface IWafIngestionStore
{
    /// <summary>Parsea el CSV de Advisor → filas + métricas. Puro (sin BD). Port de parse_advisor_csv.</summary>
    (IReadOnlyList<AdvisorRow> Rows, WafIngestionMetrics Metrics) ParseAdvisorCsv(byte[] content);

    /// <summary>
    /// Ingiere las filas en una transacción: crea/actualiza canónicas (con dedup opcional),
    /// recomendaciones y findings; resuelve los findings ausentes (salvo los manuales 'importado');
    /// recalcula resource_count, renumera matrix_codes y cierra la corrida. Port de ingest_advisor_rows.
    /// </summary>
    Task<WafIngestionResult> IngestAdvisorRowsAsync(
        int clientId, string sourceName, IReadOnlyList<AdvisorRow> rows, string? createdBy,
        WafIngestionMetrics? metrics, IReadOnlyList<string>? replaceSubscriptionIds,
        Func<SqlConnection, SqlTransaction, AdvisorRow, Task<int?>>? dedupResolver,
        IReadOnlyList<object>? warnings = null, IReadOnlyList<object>? subscriptionResults = null,
        string source = "advisor", CancellationToken ct = default);

    /// <summary>Cierra como 'failed' las corridas colgadas en 'running' (port de mark_stale_ingestion_runs_failed).</summary>
    Task<int> MarkStaleRunsFailedAsync(int maxAgeHours = 24, CancellationToken ct = default);
}
