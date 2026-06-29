using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Consolidación post-curación. Port 1:1 de app/waf/consolidate.py (plan_consolidation +
/// apply_consolidation). Compara TODAS las canónicas entre sí (la curación IA puede reasignar
/// pilares), fusiona automáticamente las gemelas del mismo pilar con ratio alto, y deja a la IA
/// los casos dudosos. Usa WafText (mismos algoritmos que dedup) y WafConstants (thresholds).
/// </summary>
public sealed class WafConsolidationService(ILogger<WafConsolidationService> logger) : IWafConsolidationService
{
    public async Task<IReadOnlyList<WafMergePlan>> PlanConsolidationAsync(
        IReadOnlyList<WafCandidate> candidates, Func<WafCandidate, WafCandidate, Task<bool>>? aiConfirm = null)
    {
        // Orden de preferencia del primary: manual < auto, con-progreso < sin, id ascendente.
        var ordered = candidates
            .OrderBy(c => IsManual(c) ? 0 : 1)
            .ThenBy(c => (c.CompletionPct ?? 0) > 0 ? 0 : 1)
            .ThenBy(c => c.CanonicalId)
            .ToList();

        var kept = new List<WafCandidate>();
        var merges = new List<WafMergePlan>();

        foreach (var candidate in ordered)
        {
            var candTitle = WafText.NormalizeText(candidate.ReviewScopeEs);
            var candResources = WafText.ResourceSet(candidate.ResourcesText);
            var candTokens = WafText.TitleTokens(candidate.ReviewScopeEs);

            WafCandidate? best = null;
            double bestScore = -1, bestRatio = 0;
            var bestTriggersAi = false;

            foreach (var primary in kept)
            {
                var ratio = WafText.SequenceRatio(candTitle, WafText.NormalizeText(primary.ReviewScopeEs));
                var overlap = WafText.ResourceOverlap(candResources, WafText.ResourceSet(primary.ResourcesText));
                var jaccard = WafText.Jaccard(candTokens, WafText.TitleTokens(primary.ReviewScopeEs));
                var triggersAi = ratio >= WafConstants.AiTitleRatio
                    || overlap >= WafConstants.AiResourceOverlap
                    || jaccard >= WafConstants.AiTokenJaccard;
                var score = Math.Max(ratio, Math.Max(overlap, jaccard));
                if (score > bestScore)
                {
                    best = primary; bestScore = score; bestRatio = ratio; bestTriggersAi = triggersAi;
                }
            }

            if (best is null)
            {
                kept.Add(candidate);
                continue;
            }

            var samePillar = candidate.PillarNumber == best.PillarNumber;
            if (samePillar && bestRatio >= WafConstants.AutoMergeRatio)
            {
                merges.Add(new WafMergePlan(best, candidate, bestRatio, "deterministic"));
            }
            else if (bestTriggersAi && aiConfirm is not null && await aiConfirm(candidate, best))
            {
                merges.Add(new WafMergePlan(best, candidate, bestRatio, "azure_openai"));
            }
            else
            {
                kept.Add(candidate);
            }
        }
        return merges;
    }

    public async Task<int> ApplyConsolidationAsync(
        SqlConnection conn, SqlTransaction tx, IReadOnlyList<WafMergePlan> merges, string? actor, CancellationToken ct = default)
    {
        var merged = 0;
        foreach (var m in merges)
        {
            if (m.Duplicate.RecommendationId is not { } dupRec || m.Primary.RecommendationId is not { } primaryRec)
            {
                logger.LogWarning("Consolidación: candidato sin recommendation_id, omitido");
                continue;
            }

            // 1) Borrar hallazgos del duplicado que YA existen en el primary (mismo recurso+suscripción).
            await Exec(conn, tx, ct, """
                DELETE f FROM dbo.waf_resource_finding f
                WHERE f.recommendation_id = @dup
                  AND EXISTS (SELECT 1 FROM dbo.waf_resource_finding p
                              WHERE p.recommendation_id = @primary
                                AND p.resource_name = f.resource_name AND p.subscription_id = f.subscription_id)
                """, ("@dup", dupRec), ("@primary", primaryRec));

            // 2) Reasignar los hallazgos restantes al primary.
            await Exec(conn, tx, ct,
                "UPDATE dbo.waf_resource_finding SET recommendation_id = @primary WHERE recommendation_id = @dup",
                ("@primary", primaryRec), ("@dup", dupRec));

            // 3) Descartar la recomendación duplicada (con auditoría del motivo).
            await Exec(conn, tx, ct, """
                UPDATE dbo.waf_recommendation
                SET is_dismissed = 1, is_active = 0, dismissed_at = SYSUTCDATETIME(), dismissed_by = @actor,
                    dismissal_reason = 'Consolidada con ' + (SELECT matrix_code FROM dbo.waf_recommendation WHERE recommendation_id = @primary)
                WHERE recommendation_id = @dup
                """, ("@actor", (object?)actor ?? DBNull.Value), ("@primary", primaryRec), ("@dup", dupRec));

            // 4) Marcar el grafo de auditoría en el catálogo.
            await Exec(conn, tx, ct,
                "UPDATE dbo.waf_recommendation_canonical SET consolidates_to_id = @primaryCanon, updated_at = SYSUTCDATETIME() WHERE canonical_id = @dupCanon",
                ("@primaryCanon", m.Primary.CanonicalId), ("@dupCanon", m.Duplicate.CanonicalId));

            // 5) Recalcular resource_count del primary (solo hallazgos activos).
            await Exec(conn, tx, ct, """
                UPDATE dbo.waf_recommendation
                SET resource_count = (SELECT COUNT(*) FROM dbo.waf_resource_finding f WHERE f.recommendation_id = @primary AND f.status = 'active')
                WHERE recommendation_id = @primary
                """, ("@primary", primaryRec));

            merged++;
        }
        return merged;
    }

    private static bool IsManual(WafCandidate c) =>
        (c.AdvisorCategory ?? "").Trim().ToLowerInvariant().StartsWith("matriz historica");

    private static async Task Exec(SqlConnection conn, SqlTransaction tx, CancellationToken ct, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.Add(new SqlParameter(n, v));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
