using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Catálogo central de recomendaciones canónicas (waf_recommendation_canonical) + aliases de
/// deduplicación (waf_canonical_alias). Port de la lógica de canónicas de app/waf/ingestion.py
/// (_get_or_create_canonical, _pillar) y de los helpers de catálogo/alias de app/routes/waf.py.
///
/// Convención de transacciones: los métodos sin (conn, tx) abren su propia conexión y aseguran el
/// schema (lazy). Los métodos con (conn, tx) participan en la transacción de la corrida de ingesta
/// (los abre el caller, que YA aseguró el schema) — NO abren conexión ni re-aseguran schema.
/// </summary>
public interface IWafCatalogStore
{
    /// <summary>
    /// Busca por (advisor_name, advisor_category) case-insensitive; si no existe intenta el
    /// resolver de dedup (alias/IA) y, si no hay match, crea una canónica nueva con
    /// ai_review_status='pending'. Port fiel de _get_or_create_canonical. Participa en la
    /// transacción de ingesta. Devuelve el id, su pilar y si se creó.
    ///
    /// <paramref name="source"/> es el origen de la ingesta ('advisor' | 'csv' | 'excel'): solo
    /// 'advisor' (la API ARM, que responde en inglés) puede sembrar advisor_name_en.
    /// </summary>
    Task<(int CanonicalId, byte Pillar, bool Created)> GetOrCreateCanonicalAsync(
        SqlConnection conn, SqlTransaction tx, AdvisorRow row,
        Func<SqlConnection, SqlTransaction, AdvisorRow, Task<int?>>? dedupResolver,
        string source, CancellationToken ct);

    /// <summary>Lee una canónica por id (abre conexión propia). Null si no existe.</summary>
    Task<WafCanonical?> GetCanonicalAsync(int canonicalId, CancellationToken ct = default);

    /// <summary>
    /// Lista el catálogo global de canónicas, con filtros opcionales por estado de revisión IA
    /// (COALESCE(ai_review_status,'pending')) y por exclusión. Port de list_waf_canonical_catalog.
    /// </summary>
    Task<IReadOnlyList<WafCanonical>> ListCatalogAsync(
        string? reviewStatus, bool? excluded, CancellationToken ct = default);

    /// <summary>
    /// UPDATE dinámico de una canónica: solo aplica las columnas presentes en `fields` que estén
    /// en la whitelist interna; refresca updated_at. Los nombres de columna nunca vienen del
    /// usuario. Devuelve true si actualizó alguna fila. Port de update_waf_canonical_catalog
    /// (la validación de negocio la hace el controller).
    /// </summary>
    Task<bool> UpdateCatalogAsync(
        int canonicalId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);

    /// <summary>
    /// Guarda un alias aprendido (firma Advisor → canónica) de forma idempotente (IF NOT EXISTS
    /// contra el índice único de firma). Participa en la transacción de ingesta. Port del INSERT
    /// de alias de _build_advisor_dedup_resolver.
    /// </summary>
    Task SaveAliasAsync(
        SqlConnection conn, SqlTransaction tx, string advisorName, string advisorCategory,
        int canonicalId, string? matchSource, decimal? confidence, string? createdBy, CancellationToken ct);

    /// <summary>
    /// Busca un alias por firma (advisor_name, advisor_category) case-insensitive. Participa en la
    /// transacción de ingesta. Null si no hay alias. Port del SELECT de alias del resolver.
    /// </summary>
    Task<int?> FindAliasAsync(
        SqlConnection conn, SqlTransaction tx, string advisorName, string advisorCategory, CancellationToken ct);

    /// <summary>
    /// Catálogo activo del cliente como candidatos de dedup/consolidación (manual + Advisor previo,
    /// no excluidos ni descartados), con resources_text agregado. Participa en la transacción de
    /// ingesta. Port de _fetch_excel_match_candidates.
    /// </summary>
    Task<IReadOnlyList<WafCandidate>> LoadClientCandidatesAsync(
        SqlConnection conn, SqlTransaction tx, int clientId, CancellationToken ct);

    /// <summary>
    /// Canónica cuyos findings (de cualquier cliente) llevan este recommendationTypeId en
    /// additional_info. Null si ninguna o si el tipo aparece en más de una canónica (ambiguo).
    /// Atajo determinista de dedup: mismo tipo ARM = misma recomendación aunque cambie el label.
    /// </summary>
    Task<int?> FindCanonicalByTypeIdAsync(
        SqlConnection conn, SqlTransaction tx, string typeId, CancellationToken ct);

    /// <summary>
    /// recommendationTypeIds (lower, distinct) presentes en los findings de una canónica, de
    /// cualquier cliente. Vacío si la canónica no tiene tipos conocidos (CSV/manual).
    /// </summary>
    Task<IReadOnlyList<string>> GetCanonicalTypeIdsAsync(
        SqlConnection conn, SqlTransaction tx, int canonicalId, CancellationToken ct);

    /// <summary>
    /// Persiste una sugerencia de curación IA en la canónica (todos los campos curados + ai_*):
    /// decision→ai_review_status (include→applied, exclude→excluded, review→requires_review),
    /// is_excluded según decision, ai_reviewed_by/at. Port de _apply_ai_suggestion. Participa en la
    /// transacción provista por el caller (curación batch / apply PATCH).
    /// </summary>
    Task ApplyAiSuggestionAsync(
        SqlConnection conn, SqlTransaction tx, int canonicalId, WafCurationResult suggestion,
        string? actor, CancellationToken ct);

    /// <summary>
    /// Lista las canónicas con ai_review_status='pending' del cliente (activas, no descartadas),
    /// priorizando preferredCanonicalIds (recién creadas) y completando con el resto, hasta limit.
    /// Port de _pending_ai_canonical_ids. Participa en la transacción del caller.
    /// </summary>
    Task<IReadOnlyList<int>> ListPendingCanonicalIdsAsync(
        SqlConnection conn, SqlTransaction tx, int clientId,
        IReadOnlyList<int> preferredCanonicalIds, int limit, CancellationToken ct);

    /// <summary>
    /// Lista las primeras (TOP) canónicas globales con ai_review_status='pending' (cualquier
    /// cliente), orden por canonical_id. Port del SELECT de analyze_all_waf_recommendations.
    /// Abre conexión propia (schema lazy).
    /// </summary>
    Task<IReadOnlyList<int>> ListGlobalPendingCanonicalIdsAsync(int limit, CancellationToken ct = default);
}
