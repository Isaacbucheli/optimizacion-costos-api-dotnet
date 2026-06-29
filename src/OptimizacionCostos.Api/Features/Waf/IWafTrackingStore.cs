namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Seguimiento operativo de recomendaciones WAF (port de app/waf/tracking.py + las queries de
/// historia/comentarios que el FastAPI ejecuta desde app/routes/waf.py).
///
/// PK compuesta (client_id, canonical_id): el acceso es SIEMPRE por ambos valores.
/// Schema lazy: cada método llama <c>WafSchema.EnsureWafSchemaAsync</c> al inicio.
/// </summary>
public interface IWafTrackingStore
{
    /// <summary>
    /// Carga el estado actual de tracking. Si no existe fila, devuelve valores default
    /// (completion_pct=0, resto NULL) — nunca <c>null</c> (paridad con <c>load_tracking</c>,
    /// que siempre retorna un dict).
    /// </summary>
    Task<WafTracking> LoadAsync(int clientId, int canonicalId, CancellationToken ct = default);

    /// <summary>
    /// Aplica un update parcial (MERGE upsert) y registra una fila en <c>waf_tracking_history</c>
    /// por cada campo que realmente cambió. Devuelve los nombres (snake_case) de los campos
    /// cambiados. Port 1:1 de <c>apply_tracking_update</c>.
    /// </summary>
    Task<IReadOnlyList<string>> ApplyUpdateAsync(
        int clientId, int canonicalId, WafTrackingUpdate values, string? updatedBy, CancellationToken ct = default);

    /// <summary>Historial de cambios, más reciente primero (ORDER BY changed_at DESC).</summary>
    Task<IReadOnlyList<WafTrackingHistory>> GetHistoryAsync(
        int clientId, int canonicalId, int limit = 100, CancellationToken ct = default);

    /// <summary>Inserta un comentario (append-only) y devuelve su comment_id.</summary>
    Task<long> AddCommentAsync(
        int clientId, int canonicalId, string commentText, string userDisplay, CancellationToken ct = default);

    /// <summary>Comentarios de la recomendación, más reciente primero (ORDER BY created_at DESC).</summary>
    Task<IReadOnlyList<WafComment>> GetCommentsAsync(
        int clientId, int canonicalId, int limit = 50, CancellationToken ct = default);
}
