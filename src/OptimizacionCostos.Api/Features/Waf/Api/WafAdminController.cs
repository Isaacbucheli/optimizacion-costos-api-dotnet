using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Waf.Api;

/// <summary>
/// FASE 4 — Endpoints administrativos del módulo WAF (solo rol admin). Port de los endpoints
/// /waf/admin/* de app/routes/waf.py: configuración IA, refresh de Advisor Score, curación IA
/// (analyze / analyze-all / apply) y administración del catálogo global de canónicas.
/// </summary>
[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("waf/admin")]
public sealed class WafAdminController(
    ISqlConnectionFactory factory,
    IWafCatalogStore catalog,
    IWafCuratorService curator,
    IAdvisorScoreService advisorScore,
    ILogger<WafAdminController> logger) : ControllerBase
{
    private static readonly IReadOnlySet<string> AllowedReviewStatuses = new HashSet<string>(StringComparer.Ordinal)
        { "pending", "reviewed", "applied", "requires_review", "excluded" };

    private string? Email => User.FindFirst("sub")?.Value;

    // ==================== IA CONFIG ====================

    /// <summary>GET ai/config. Port de waf_ai_config.</summary>
    [HttpGet("ai/config")]
    public IActionResult AiConfig()
    {
        var (configured, deployment, apiVersion, hasKey) = curator.GetConfigStatus();
        return Ok(new { configured, deployment, api_version = apiVersion, has_key = hasKey });
    }

    // ==================== ADVISOR SCORE REFRESH ====================

    /// <summary>POST advisor-score/refresh. Port de refresh_waf_advisor_score.</summary>
    [HttpPost("advisor-score/refresh")]
    public async Task<IActionResult> RefreshAdvisorScore([FromBody] WafAdvisorScoreRefreshRequest? payload, CancellationToken ct)
    {
        payload ??= new WafAdvisorScoreRefreshRequest();
        try
        {
            if (payload.ClientId is int cid)
            {
                var snapshot = await advisorScore.RefreshClientAsync(
                    cid, snapshotDate: null, source: "manual", includeInReports: payload.IncludeInReports, ct);
                return Ok(new
                {
                    message = "Advisor Score actualizado",
                    clients_total = 1,
                    clients_refreshed = 1,
                    clients_failed = 0,
                    results = new[] { SnapshotSummary(snapshot) },
                });
            }

            var result = await advisorScore.RefreshAllAsync(
                clientIds: null, source: "manual", includeInReports: payload.IncludeInReports, ct);
            return Ok(new
            {
                message = "Refresh Advisor Score completado",
                clients_total = result.ClientsTotal,
                clients_refreshed = result.ClientsRefreshed,
                clients_failed = result.ClientsFailed,
                results = result.Results.Select(r => new
                {
                    client_id = r.ClientId,
                    status = r.Status,
                    snapshot_date = r.SnapshotDate,
                    captured_at = r.CapturedAt,
                    subscriptions_scored = r.SubscriptionsScored,
                }),
            });
        }
        catch (ArgumentException ex) // cliente inexistente (port de ValueError → 404)
        {
            return NotFound(new { detail = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { detail = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Advisor Score manual refresh failed");
            return Problem(statusCode: 500, detail: $"Advisor Score refresh failed: {ex.GetType().Name}");
        }
    }

    // ==================== CURACIÓN IA ====================

    /// <summary>POST ai/recommendations/{id}/analyze — analiza 1 canónica (no persiste). Port de analyze_waf_recommendation.</summary>
    [HttpPost("ai/recommendations/{canonicalId:int}/analyze")]
    public async Task<IActionResult> Analyze(int canonicalId, CancellationToken ct)
    {
        var canonical = await catalog.GetCanonicalAsync(canonicalId, ct);
        if (canonical is null) return NotFound(new { detail = "Canonical recommendation not found" });

        try
        {
            var suggestion = await curator.AnalyzeAsync(canonical, ct);
            return Ok(new { canonical_id = canonicalId, suggestion = SuggestionDto(suggestion) });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fallo el analisis WAF con IA para canonical_id={Kid}", canonicalId);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { detail = "El servicio de analisis con IA no esta disponible en este momento" });
        }
    }

    /// <summary>POST ai/recommendations/analyze-all — analiza un lote global (opcionalmente aplica). Port de analyze_all_waf_recommendations.</summary>
    [HttpPost("ai/recommendations/analyze-all")]
    public async Task<IActionResult> AnalyzeAll([FromBody] WafAiBatchRequest payload, CancellationToken ct)
    {
        var canonicalIds = await catalog.ListGlobalPendingCanonicalIdsAsync(payload.Limit, ct);
        var results = new List<object>();
        var errors = new List<object>();

        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var canonicalId in canonicalIds)
            {
                try
                {
                    var canonical = await GetCanonicalInTxAsync(conn, tx, canonicalId, ct);
                    if (canonical is null) { errors.Add(new { canonical_id = canonicalId, error = "Canonical recommendation not found" }); continue; }
                    var suggestion = await curator.AnalyzeAsync(canonical, ct);
                    if (payload.Apply)
                        await catalog.ApplyAiSuggestionAsync(conn, tx, canonicalId, suggestion, Email, ct);
                    results.Add(new { canonical_id = canonicalId, suggestion = SuggestionDto(suggestion) });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors.Add(new { canonical_id = canonicalId, error = Truncate(ex.Message, 500) });
                }
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return Ok(new
        {
            total = canonicalIds.Count,
            processed = results.Count,
            applied = payload.Apply ? results.Count : 0,
            errors,
        });
    }

    /// <summary>PATCH ai/recommendations/{id}/apply — aplica una sugerencia IA editada. Port de apply_waf_ai_suggestion.</summary>
    [HttpPatch("ai/recommendations/{canonicalId:int}/apply")]
    public async Task<IActionResult> ApplySuggestion(int canonicalId, [FromBody] WafAiSuggestionRequest payload, CancellationToken ct)
    {
        if (payload.Decision == "exclude" && string.IsNullOrWhiteSpace(payload.ExclusionReason))
            return BadRequest(new { detail = "Exclusion reason is required" });

        var canonical = await catalog.GetCanonicalAsync(canonicalId, ct);
        if (canonical is null) return NotFound(new { detail = "Canonical recommendation not found" });

        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await catalog.ApplyAiSuggestionAsync(conn, tx, canonicalId, payload.ToCurationResult(), Email, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
        return Ok(new { message = "Sugerencia IA aplicada", canonical_id = canonicalId });
    }

    // ==================== CATÁLOGO GLOBAL ====================

    /// <summary>GET catalog — catálogo global de canónicas. Port de list_waf_canonical_catalog.</summary>
    [HttpGet("catalog")]
    public async Task<IActionResult> ListCatalog(
        [FromQuery(Name = "review_status")] string? reviewStatus, [FromQuery] bool? excluded, CancellationToken ct)
    {
        var items = await catalog.ListCatalogAsync(reviewStatus, excluded, ct);
        return Ok(items.Select(c => new
        {
            canonical_id = c.CanonicalId,
            advisor_name = c.AdvisorName,
            advisor_category = c.AdvisorCategory,
            pillar_number = c.PillarNumber,
            review_scope_es = c.ReviewScopeEs,
            benefit_es = c.BenefitEs,
            client_action_es = c.ClientActionEs,
            bit_action_es = c.BitActionEs,
            is_excluded = c.IsExcluded,
            exclusion_reason = c.ExclusionReason,
            consolidates_to_id = c.ConsolidatesToId,
            ai_review_status = string.IsNullOrEmpty(c.AiReviewStatus) ? "pending" : c.AiReviewStatus,
            ai_decision = c.AiDecision,
            ai_confidence = c.AiConfidence,
            ai_possible_additional_cost = c.AiPossibleAdditionalCost ?? false,
            ai_cost_reason = c.AiCostReason,
            ai_exclusion_reason = c.AiExclusionReason,
            ai_duplicate_group_key = c.AiDuplicateGroupKey,
            ai_reviewed_at = c.AiReviewedAt,
            created_at = c.CreatedAt,
            updated_at = c.UpdatedAt,
        }));
    }

    /// <summary>PUT catalog/{id} — edita una canónica directamente. Port de update_waf_canonical_catalog.</summary>
    [HttpPut("catalog/{canonicalId:int}")]
    public async Task<IActionResult> UpdateCatalog(int canonicalId, [FromBody] WafCanonicalUpdateRequest payload, CancellationToken ct)
    {
        if (!payload.AnySet)
            return BadRequest(new { detail = "No catalog fields were provided" });

        // Normaliza (port de _normalize_canonical_update).
        if (payload.AiReviewStatusSet && payload.AiReviewStatus is not null && !AllowedReviewStatuses.Contains(payload.AiReviewStatus))
            return BadRequest(new { detail = "Invalid review status" });

        var isExcluded = payload.IsExcludedSet ? payload.IsExcluded : null;
        if (payload.AiReviewStatusSet && payload.AiReviewStatus == "excluded")
            isExcluded = true;

        var exclusionReason = payload.ExclusionReasonSet ? payload.ExclusionReason : null;
        if (isExcluded == true && string.IsNullOrWhiteSpace(exclusionReason))
            return BadRequest(new { detail = "Exclusion reason is required" });

        var canonical = await catalog.GetCanonicalAsync(canonicalId, ct);
        if (canonical is null) return NotFound(new { detail = "Canonical recommendation not found" });

        // Solo las columnas presentes (semántica exclude_unset) entran al UPDATE.
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (payload.PillarNumberSet) fields["pillar_number"] = payload.PillarNumber;
        if (payload.ReviewScopeEsSet) fields["review_scope_es"] = payload.ReviewScopeEs;
        if (payload.BenefitEsSet) fields["benefit_es"] = payload.BenefitEs;
        if (payload.ClientActionEsSet) fields["client_action_es"] = payload.ClientActionEs;
        if (payload.BitActionEsSet) fields["bit_action_es"] = payload.BitActionEs;
        if (payload.AiReviewStatusSet) fields["ai_review_status"] = payload.AiReviewStatus;
        if (payload.ExclusionReasonSet) fields["exclusion_reason"] = payload.ExclusionReason;
        // is_excluded puede haberse forzado a true por ai_review_status='excluded'.
        if (isExcluded is bool b) fields["is_excluded"] = b;

        await catalog.UpdateCatalogAsync(canonicalId, fields, ct);
        return Ok(new { message = "Catalogo WAF actualizado", canonical_id = canonicalId });
    }

    // ==================== HELPERS ====================

    private static object SuggestionDto(WafCurationResult s) => new
    {
        decision = s.Decision,
        possible_additional_cost = s.PossibleAdditionalCost,
        cost_reason = s.CostReason,
        duplicate_group_key = s.DuplicateGroupKey,
        pillar_number = s.PillarNumber,
        review_scope_es = s.ReviewScopeEs,
        benefit_es = s.BenefitEs,
        client_action_es = s.ClientActionEs,
        bit_action_es = s.BitActionEs,
        exclusion_reason = s.ExclusionReason,
        confidence = s.Confidence,
        raw_model_text = s.RawModelText,
    };

    private static object SnapshotSummary(WafAdvisorScoreSnapshot? s) => s is null ? new { } : new
    {
        client_id = s.ClientId,
        status = s.Status,
        snapshot_date = s.SnapshotDate.ToString("yyyy-MM-dd"),
        captured_at = s.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
        subscriptions_scored = s.SubscriptionsScored,
    };

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
                   ai_reviewed_by, ai_reviewed_at, created_at, updated_at
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
            UpdatedAt: r.GetDateTime(22));
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];
}
