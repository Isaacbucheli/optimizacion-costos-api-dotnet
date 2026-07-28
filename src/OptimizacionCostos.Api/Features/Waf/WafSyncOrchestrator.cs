using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// FASE 3 — Implementación del orquestador WAF. Solo compone los servicios/stores existentes;
/// no contiene lógica de negocio nueva (esa vive en los servicios de la FASE 2). Port de la
/// orquestación que el FastAPI tenía inline en app/routes/waf.py.
/// </summary>
public sealed class WafSyncOrchestrator(
    ISqlConnectionFactory factory,
    IWafIngestionStore ingestion,
    IWafDedupResolverFactory resolverFactory,
    IWafDedupService dedup,
    IWafCatalogStore catalog,
    IWafRecommendationStore recommendations,
    IWafConsolidationService consolidation,
    IWafCuratorService curator,
    IAdvisorScoreService advisorScore,
    IAdvisorScoreStore advisorScoreStore,
    ILogger<WafSyncOrchestrator> logger) : IWafSyncOrchestrator
{
    // Topes de confirmación de duplicado con Azure OpenAI por corrida (port de las constantes de waf.py).
    private const int AdvisorDedupAiLimitSync = 60;
    private const int AdvisorDedupAiLimitCsv = 20;
    private const int AdvisorSyncAiReviewLimit = 50;
    private const int ConsolidationAiLimit = 80;

    // -------------------- CSV (port de ingest_advisor_csv / upload_waf_ingestion) --------------------

    public async Task<(WafIngestionResult Result, int Merged)> RunCsvAsync(
        int clientId, string fileName, byte[] content, string? createdBy, CancellationToken ct = default)
    {
        var (rows, metrics) = ingestion.ParseAdvisorCsv(content);
        var resolver = resolverFactory.Build(clientId, createdBy, AdvisorDedupAiLimitCsv, out var state);

        var result = await ingestion.IngestAdvisorRowsAsync(
            clientId, fileName, rows, createdBy,
            metrics: metrics,
            replaceSubscriptionIds: null, // carga manual: no resuelve hallazgos previos
            dedupResolver: resolver,
            warnings: null,
            source: "csv",
            ct: ct);

        // merged_duplicates sale del estado del resolver (paridad con
        // result["merged_duplicates"] = dedup_resolver.state["merged"]).
        return (result, state.Merged);
    }

    // -------------------- Advisor sync (port de _run_advisor_sync_job) --------------------

    public async Task<WafAdvisorSyncResult> RunAdvisorSyncAsync(
        int clientId, IReadOnlyList<string>? subscriptions, string? createdBy,
        int timeoutSecondsPerSubscription,
        Func<WafAdvisorSyncProgress, CancellationToken, Task>? progress = null,
        CancellationToken ct = default)
    {
        var groups = await advisorScoreStore.LoadClientSubscriptionGroupsAsync(clientId, ct);

        // Filtra a las suscripciones pedidas (si se especificaron). La validación de que existan
        // la hace el controller; aquí solo se procesan las gestionadas.
        var requested = subscriptions is { Count: > 0 }
            ? new HashSet<string>(subscriptions, StringComparer.OrdinalIgnoreCase)
            : null;

        var rows = new List<AdvisorRow>();
        var warnings = new List<object>();
        var subscriptionResults = new List<object>();
        var successfulSubscriptions = new List<string>();
        var subsTotal = 0;
        var subsFailed = 0;
        var expectedTotal = groups.Values.Sum(group => group.Subscriptions.Keys.Count(subscriptionId =>
            requested is null || requested.Contains(subscriptionId)));

        foreach (var (_, group) in groups)
        {
            foreach (var (subscriptionId, subscriptionName) in group.Subscriptions)
            {
                if (requested is not null && !requested.Contains(subscriptionId)) continue;
                subsTotal++;
                try
                {
                    var (subRows, subMetrics) = await advisorScore.GenerateAndListRecommendationsAsync(
                        group.CredentialId, subscriptionId, subscriptionName,
                        timeoutSecondsPerSubscription, ct);

                    // Cross-check Defender (regla del portal de Advisor): poda las filas de
                    // Seguridad cuyo assessment ya no está vigente. Fail-open: set null => no filtra.
                    var applicable = await advisorScore.FetchApplicableAssessmentTypesAsync(
                        group.CredentialId, subscriptionId, ct);
                    var (keptRows, defenderCheck, defenderSkipped) = ApplyDefenderCrossCheck(subRows, applicable);
                    if (defenderCheck == "unavailable")
                        logger.LogWarning("Cross-check Defender no disponible subscription_id={Sid}; se ingesta sin filtrar", subscriptionId);
                    else if (defenderSkipped > 0)
                        logger.LogInformation("Cross-check Defender omitió {N} recomendaciones de seguridad no vigentes subscription_id={Sid}", defenderSkipped, subscriptionId);

                    rows.AddRange(keptRows);
                    successfulSubscriptions.Add(subscriptionId);
                    subscriptionResults.Add(new
                    {
                        credential_name = group.CredentialName,
                        subscription_id = subscriptionId,
                        subscription_name = subscriptionName,
                        status = subMetrics.PaginationTruncated ? "partial" : "ok",
                        error = (string?)null,
                        defender_check = defenderCheck,
                        defender_resolved_skipped = defenderSkipped,
                        suppressed_skipped = subMetrics.RowsSuppressedSkipped,
                    });
                }
                catch (Exception ex)
                {
                    subsFailed++;
                    logger.LogError(ex, "Azure Advisor sync falló client_id={Cid} subscription_id={Sid} type={Type}",
                        clientId, subscriptionId, ex.GetType().Name);
                    warnings.Add(new
                    {
                        credential_id = group.CredentialId,
                        credential_name = group.CredentialName,
                        subscription_id = subscriptionId,
                        subscription_name = subscriptionName,
                        error = $"{ex.GetType().Name}: {Truncate(ex.Message, 500)}",
                    });
                    subscriptionResults.Add(new
                    {
                        credential_name = group.CredentialName,
                        subscription_id = subscriptionId,
                        subscription_name = subscriptionName,
                        status = "error",
                        error = $"{ex.GetType().Name}: {Truncate(ex.Message, 500)}",
                    });
                }
                if (progress is not null)
                    await progress(new WafAdvisorSyncProgress(
                        successfulSubscriptions.Count + subsFailed, subsFailed, expectedTotal, subscriptionName), ct);
            }
        }

        if (successfulSubscriptions.Count == 0)
        {
            // No se pudo generar en ninguna suscripción: cierra una corrida fallida.
            return new WafAdvisorSyncResult(
                RunId: 0, Status: "failed",
                SubscriptionsQueued: subsTotal, SubscriptionsProcessed: 0, SubscriptionsFailed: subsFailed,
                NewRecommendations: 0, NewFindings: 0, ResolvedFindings: 0, MergedDuplicates: 0,
                AiProcessed: 0, AiErrors: 0, Warnings: warnings);
        }

        var resolver = resolverFactory.Build(clientId, createdBy, AdvisorDedupAiLimitSync, out var state);
        var ingestionResult = await ingestion.IngestAdvisorRowsAsync(
            clientId, "Azure Advisor API", rows, createdBy,
            metrics: null,
            replaceSubscriptionIds: successfulSubscriptions,
            dedupResolver: resolver,
            warnings: warnings,
            subscriptionResults: subscriptionResults,
            source: "advisor",
            ct: ct);

        var mergedDuplicates = state.Merged;

        // Tras curar (traducir Advisor al espanol) se consolidan duplicados que quedaron con titulo
        // casi identico al de una recomendacion ya cargada.
        var curation = await CurateClientAsync(
            clientId, AdvisorSyncAiReviewLimit, ingestionResult.NewCanonicalIds, createdBy, ct);
        var consolidationResult = await ConsolidateAsync(clientId, useAi: true, actor: createdBy, ct: ct);
        mergedDuplicates += consolidationResult.Merged;

        return new WafAdvisorSyncResult(
            RunId: ingestionResult.RunId,
            Status: "completed",
            SubscriptionsQueued: subsTotal,
            SubscriptionsProcessed: successfulSubscriptions.Count,
            SubscriptionsFailed: subsFailed,
            NewRecommendations: ingestionResult.NewRecommendations,
            NewFindings: ingestionResult.NewFindings,
            ResolvedFindings: ingestionResult.ResolvedFindings,
            MergedDuplicates: mergedDuplicates,
            AiProcessed: curation.Processed,
            AiErrors: curation.Errors,
            Warnings: warnings);
    }

    /// <summary>
    /// Aplica el filtro Defender a las filas de una suscripción y resume el resultado para
    /// subscription_results. Set null => cross-check "unavailable" y NO se filtra (fail-open).
    /// </summary>
    internal static (IReadOnlyList<AdvisorRow> Rows, string DefenderCheck, int DefenderSkipped) ApplyDefenderCrossCheck(
        IReadOnlyList<AdvisorRow> subRows, IReadOnlySet<string>? applicableTypes)
    {
        if (applicableTypes is null)
            return (subRows, "unavailable", 0);
        var (kept, dropped) = AdvisorApiClient.FilterSecurityRowsByDefender(subRows, applicableTypes);
        return (kept, "ok", dropped);
    }

    // -------------------- Consolidación (port de _consolidate_client_duplicates) --------------------

    public async Task<WafConsolidationResult> ConsolidateAsync(
        int clientId, bool useAi, string? actor, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var candidates = await catalog.LoadClientCandidatesAsync(conn, tx, clientId, ct);
            if (candidates.Count < 2)
            {
                await tx.CommitAsync(ct);
                return new WafConsolidationResult(Merged: 0, AiCalls: 0);
            }

            var aiConfigured = useAi && curator.GetConfigStatus().Configured;
            var aiCalls = 0;

            Func<WafCandidate, WafCandidate, Task<bool>>? aiConfirm = null;
            if (aiConfigured)
            {
                aiConfirm = async (duplicate, primary) =>
                {
                    if (aiCalls >= ConsolidationAiLimit) return false;
                    aiCalls++;
                    // Compara el título ES de la duplicada contra el candidato primario (port exacto).
                    var result = await dedup.AiConfirmDuplicateAsync(
                        duplicate.ReviewScopeEs ?? "",
                        duplicate.AdvisorCategory ?? "",
                        new[]
                        {
                            new WafCandidate(
                                CanonicalId: primary.CanonicalId,
                                RecommendationId: primary.RecommendationId,
                                MatrixCode: primary.MatrixCode,
                                PillarNumber: primary.PillarNumber,
                                AdvisorName: primary.ReviewScopeEs ?? "",
                                AdvisorCategory: primary.AdvisorCategory ?? "",
                                ReviewScopeEs: primary.ReviewScopeEs ?? "",
                                BenefitEs: primary.BenefitEs,
                                ClientActionEs: primary.ClientActionEs,
                                BitActionEs: primary.BitActionEs,
                                ResourcesText: primary.ResourcesText,
                                CompletionPct: primary.CompletionPct,
                                RemediationStartDate: primary.RemediationStartDate,
                                ExecutionLog: primary.ExecutionLog),
                        }, ct);
                    return result is not null
                        && result.CanonicalId == primary.CanonicalId
                        && (double)result.Confidence >= WafConstants.MinAiConfidence;
                };
            }

            var merges = await consolidation.PlanConsolidationAsync(candidates, aiConfirm);
            var merged = await consolidation.ApplyConsolidationAsync(conn, tx, merges, actor, ct);
            if (merged > 0)
                await recommendations.RenumberMatrixCodesAsync(conn, tx, clientId, ct);

            await tx.CommitAsync(ct);
            return new WafConsolidationResult(Merged: merged, AiCalls: aiCalls);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // -------------------- Curación IA por lotes (port de _auto_curate_client_recommendations) --------------------

    public async Task<WafBatchCurationResult> CurateClientAsync(
        int clientId, int limit, IReadOnlyList<int>? preferredCanonicalIds, string? actor, CancellationToken ct = default)
    {
        if (!curator.GetConfigStatus().Configured)
        {
            logger.LogWarning("Azure OpenAI no configurado; se omite curaduria WAF automatica client_id={Cid}", clientId);
            return new WafBatchCurationResult(Processed: 0, Errors: 0, Skipped: "not_configured");
        }

        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var canonicalIds = await catalog.ListPendingCanonicalIdsAsync(
                conn, tx, clientId, preferredCanonicalIds ?? [], limit, ct);

            var processed = 0;
            var errors = 0;
            foreach (var canonicalId in canonicalIds)
            {
                try
                {
                    var canonical = await GetCanonicalInTxAsync(conn, tx, canonicalId, ct);
                    if (canonical is null) { errors++; continue; }
                    var suggestion = await curator.AnalyzeAsync(canonical, ct);
                    await catalog.ApplyAiSuggestionAsync(conn, tx, canonicalId, suggestion, actor ?? "advisor-sync", ct);
                    processed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors++;
                    logger.LogError(ex, "Fallo curaduria automatica Advisor client_id={Cid} canonical_id={Kid} type={Type}",
                        clientId, canonicalId, ex.GetType().Name);
                }
            }

            if (processed > 0)
                await recommendations.RenumberMatrixCodesAsync(conn, tx, clientId, ct);

            await tx.CommitAsync(ct);
            return new WafBatchCurationResult(Processed: processed, Errors: errors);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>Lee una canónica dentro de la transacción de curación (equivale a _get_ai_candidate).</summary>
    private static async Task<WafCanonical?> GetCanonicalInTxAsync(
        SqlConnection conn, SqlTransaction tx, int canonicalId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT canonical_id, advisor_name, advisor_category, pillar_number,
                   review_scope_es, benefit_es, client_action_es, bit_action_es,
                   is_excluded, exclusion_reason, consolidates_to_id,
                   ai_review_status, ai_decision, ai_confidence, ai_possible_additional_cost,
                   ai_cost_reason, ai_exclusion_reason, ai_duplicate_group_key, ai_raw_model_text,
                   ai_reviewed_by, ai_reviewed_at, created_at, updated_at, advisor_name_en
            FROM dbo.waf_recommendation_canonical
            WHERE canonical_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", canonicalId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new WafCanonical(
            CanonicalId: r.GetInt32(0),
            AdvisorName: r.GetString(1),
            AdvisorCategory: r.GetString(2),
            PillarNumber: r.GetByte(3),
            ReviewScopeEs: r.GetString(4),
            BenefitEs: r.GetString(5),
            ClientActionEs: r.GetString(6),
            BitActionEs: r.GetString(7),
            IsExcluded: r.GetBoolean(8),
            ExclusionReason: r.IsDBNull(9) ? null : r.GetString(9),
            ConsolidatesToId: r.IsDBNull(10) ? null : r.GetInt32(10),
            AiReviewStatus: r.IsDBNull(11) ? null : r.GetString(11),
            AiDecision: r.IsDBNull(12) ? null : r.GetString(12),
            AiConfidence: r.IsDBNull(13) ? null : r.GetDecimal(13),
            AiPossibleAdditionalCost: r.IsDBNull(14) ? null : r.GetBoolean(14),
            AiCostReason: r.IsDBNull(15) ? null : r.GetString(15),
            AiExclusionReason: r.IsDBNull(16) ? null : r.GetString(16),
            AiDuplicateGroupKey: r.IsDBNull(17) ? null : r.GetString(17),
            AiRawModelText: r.IsDBNull(18) ? null : r.GetString(18),
            AiReviewedBy: r.IsDBNull(19) ? null : r.GetString(19),
            AiReviewedAt: r.IsDBNull(20) ? null : r.GetDateTime(20),
            CreatedAt: r.GetDateTime(21),
            UpdatedAt: r.GetDateTime(22),
            AdvisorNameEn: r.IsDBNull(23) ? null : r.GetString(23));
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}
