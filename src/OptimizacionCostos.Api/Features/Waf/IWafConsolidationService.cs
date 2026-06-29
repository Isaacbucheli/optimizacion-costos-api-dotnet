using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Consolidación de recomendaciones duplicadas de un cliente (port de app/waf/consolidate.py).
/// Tras curar (traducir) las recomendaciones de Advisor, dos canónicas gemelas quedan con texto
/// casi idéntico; aquí se detectan (título ES + recursos + tokens, con confirmación IA opcional),
/// se mueven los hallazgos a una sola canónica y se descarta la otra.
/// </summary>
public interface IWafConsolidationService
{
    /// <summary>
    /// Decide qué canónicas se fusionan (sin tocar BD). Compara TODAS entre sí (no por pilar,
    /// porque la curación IA puede reasignar el pilar). Fusión automática solo dentro del mismo
    /// pilar con ratio &gt;= AutoMergeRatio (0.93); el resto dispara IA si ratio &gt;= AiTitleRatio
    /// (0.55) o overlap &gt;= AiResourceOverlap (0.50) o jaccard &gt;= AiTokenJaccard (0.30) y
    /// <paramref name="aiConfirm"/>(duplicada, primary) devuelve true. Port de plan_consolidation.
    /// </summary>
    Task<IReadOnlyList<WafMergePlan>> PlanConsolidationAsync(
        IReadOnlyList<WafCandidate> candidates,
        Func<WafCandidate, WafCandidate, Task<bool>>? aiConfirm = null);

    /// <summary>
    /// Aplica las fusiones: mueve hallazgos del duplicado al primary (eliminando los repetidos por
    /// recurso+suscripción), descarta la recomendación duplicada, marca consolidates_to_id en el
    /// catálogo y recalcula resource_count del primary. Devuelve cuántas fusionó. Port de
    /// apply_consolidation. Usa la conexión + transacción provistas por el orquestador.
    /// </summary>
    Task<int> ApplyConsolidationAsync(
        SqlConnection conn, SqlTransaction tx, IReadOnlyList<WafMergePlan> merges,
        string? actor, CancellationToken ct = default);
}
