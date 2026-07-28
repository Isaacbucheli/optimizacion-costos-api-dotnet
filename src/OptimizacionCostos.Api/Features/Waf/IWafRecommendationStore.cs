using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Lectura de la matriz por cliente (waf_recommendation + canonical + findings) + agregados
/// (summary/sections) + descarte (dismiss) + utilidades de ingesta (recalcular resource_count y
/// renumerar matrix_code). Port de app/routes/waf.py (list/get/resources/dismiss/summary/sections)
/// y app/waf/ingestion.py (recálculo de counts + renumber_matrix_codes).
///
/// Convención de transacciones: los métodos sin (conn, tx) abren su propia conexión y aseguran el
/// schema (lazy). RecalculateResourceCountsAsync y RenumberMatrixCodesAsync reciben (conn, tx) y
/// participan en la transacción de la corrida de ingesta/consolidación que abre el caller.
/// </summary>
public interface IWafRecommendationStore
{
    /// <summary>
    /// Lista las recomendaciones del cliente (join con canónica + tracking), opcionalmente filtradas
    /// por pilar y/o solo activas. Orden: pilar, impacto, ámbito. Port de list_waf_recommendations.
    /// </summary>
    Task<IReadOnlyList<WafRecommendation>> ListAsync(
        int clientId, byte? pillar, bool activeOnly, CancellationToken ct = default);

    /// <summary>
    /// Detalle de una recomendación (por client_id + canonical_id, no descartada). Null si no existe.
    /// Devuelve la fila base (WafRecommendation); el detalle textual (canónica) y tracking los
    /// compone el controller. Port de get_waf_recommendation.
    /// </summary>
    Task<WafRecommendation?> GetAsync(int clientId, int canonicalId, CancellationToken ct = default);

    /// <summary>
    /// Findings de una recomendación (por client_id + canonical_id, recomendación no descartada),
    /// orden por status y nombre. Port de list_waf_recommendation_resources.
    /// </summary>
    Task<IReadOnlyList<WafResourceFinding>> ListResourcesAsync(
        int clientId, int canonicalId, CancellationToken ct = default);

    /// <summary>
    /// Descarta (soft-delete) una recomendación: marca findings activos como 'dismissed', la
    /// recomendación como is_dismissed/is_active=0, registra en historial y renumera matrix_codes.
    /// No-op idempotente si ya estaba descartada. Lanza KeyNotFoundException si no existe.
    /// Transacción interna. Port de dismiss_waf_recommendation.
    /// </summary>
    Task DismissAsync(
        int clientId, int canonicalId, string? reason, string? actor, CancellationToken ct = default);

    /// <summary>Marca (idempotente) la recomendación como leída por el usuario dado.</summary>
    Task MarkReadAsync(int clientId, int canonicalId, string userKey, CancellationToken ct = default);

    /// <summary>Resumen de la matriz del cliente (totales + última ingestión). Port de waf_client_summary.
    /// Si excludeSecurityPillar, los conteos omiten el pilar de seguridad (gestión externa).
    /// subscriptions vacío = sin filtro (comportamiento histórico).</summary>
    Task<WafClientSummary> GetSummaryAsync(
        int clientId, bool excludeSecurityPillar = false,
        IReadOnlyList<string>? subscriptions = null, CancellationToken ct = default);

    /// <summary>Tarjetas por pilar (siempre las 5). Port de waf_client_sections.</summary>
    Task<IReadOnlyList<WafSection>> GetSectionsAsync(
        int clientId, IReadOnlyList<string>? subscriptions = null, CancellationToken ct = default);

    /// <summary>
    /// Suscripciones del cliente **derivadas de los hallazgos activos**, con su conteo de
    /// recomendaciones y recursos. No sale de client_azure_subscriptions a propósito: los clientes
    /// migrados del CDC traen placeholders que no cruzan contra esa tabla y quedarían inalcanzables.
    /// </summary>
    Task<IReadOnlyList<WafSubscriptionOption>> ListSubscriptionsAsync(int clientId, CancellationToken ct = default);

    /// <summary>
    /// Recalcula resource_count (findings activos) e is_active (0 si descartada o sin recursos) de
    /// todas las recomendaciones del cliente. Participa en la transacción de ingesta. Port del UPDATE
    /// con CROSS APPLY de ingest_advisor_rows.
    /// </summary>
    Task RecalculateResourceCountsAsync(SqlConnection conn, SqlTransaction tx, int clientId, CancellationToken ct);

    /// <summary>
    /// Renumera matrix_code = 'pilar.secuencia' por ROW_NUMBER PARTITION BY pilar ORDER BY
    /// impact_number, review_scope_es, canonical_id (solo activas no descartadas). Participa en la
    /// transacción de ingesta. Port de renumber_matrix_codes.
    /// </summary>
    Task RenumberMatrixCodesAsync(SqlConnection conn, SqlTransaction tx, int clientId, CancellationToken ct);
}
