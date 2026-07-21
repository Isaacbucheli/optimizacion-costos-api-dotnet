using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Features.Waf.Api;

/// <summary>
/// FASE 4 — Matriz Optimización y Mejoras (WAF). Endpoints por cliente + públicos.
/// Port de app/routes/waf.py (prefix /waf). Acceso por cliente vía IAnalysisAccess; SQL
/// parametrizado; respuestas con las MISMAS claves snake_case del FastAPI.
/// </summary>
[ApiController]
[Authorize]
[Route("waf")]
public sealed class WafController(
    ISqlConnectionFactory factory,
    IAnalysisAccess access,
    IWafRecommendationStore recommendations,
    IWafTrackingStore tracking,
    IWafCatalogStore catalog,
    IWafIngestionStore ingestionStore,
    IAdvisorScoreStore advisorScoreStore,
    IWafCuratorService curator,
    IWafTranslationService translation,
    IWafExcelExporter excelExporter,
    IWafExcelImporter excelImporter,
    IClientStore clientStore,
    IWafSyncOrchestrator orchestrator,
    IWafAdvisorSyncJobStore advisorSyncJobs,
    IWafAdvisorSyncJobQueue advisorSyncQueue,
    ILogger<WafController> logger) : ControllerBase
{
    private const string CostReferenceDisclaimer =
        "Valor referencial calculado con tarifas publicas de Azure (Retail Prices). "
        + "No representa facturacion real CSP, descuentos, reservas, Savings Plans, creditos ni impuestos.";

    private static readonly IReadOnlyDictionary<int, string> PillarSectionNames = new Dictionary<int, string>
    {
        [1] = "Eficiencia del rendimiento",
        [2] = "Excelencia operacional",
        [3] = "Seguridad",
        [4] = "Confiabilidad",
        [5] = "Optimizacion de costos",
    };

    private string? Email => User.FindFirst("sub")?.Value;
    private string? UserName => User.FindFirst("name")?.Value;

    // ==================== PÚBLICO ====================

    /// <summary>GET /waf/status — health check del módulo (asegura el schema). Port de waf_status.</summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        await WafSchema.EnsureWafSchemaAsync(factory, ct);
        return Ok(new
        {
            module = "Matriz Optimizacion y Mejoras (WAF)",
            status = "ready",
            cost_module = "Matriz Optimizacion Costos Azure",
            cost_engine_reused = true,
        });
    }

    // ==================== POR CLIENTE — LECTURA ====================

    /// <summary>GET summary. Port de waf_client_summary.</summary>
    [HttpGet("clients/{clientId:int}/summary")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> Summary(int clientId, CancellationToken ct)
    {
        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        var (secManaged, _) = await clientStore.GetSecurityManagementAsync(clientId, ct);
        var s = await recommendations.GetSummaryAsync(clientId, excludeSecurityPillar: secManaged, ct);
        return Ok(new
        {
            client_id = s.ClientId,
            recommendations = s.Recommendations,
            active_recommendations = s.ActiveRecommendations,
            cost_recommendations = s.CostRecommendations,
            active_findings = s.ActiveFindings,
            latest_ingestion = s.LatestIngestion is null ? null : new
            {
                run_id = s.LatestIngestion.RunId,
                source_file_name = s.LatestIngestion.SourceFileName,
                status = s.LatestIngestion.Status,
                rows_total = s.LatestIngestion.RowsTotal,
                rows_processed = s.LatestIngestion.RowsProcessed,
                started_at = s.LatestIngestion.StartedAt,
                completed_at = s.LatestIngestion.CompletedAt,
            },
        });
    }

    /// <summary>GET sections — tarjetas por pilar (siempre 5). Port de waf_client_sections.</summary>
    [HttpGet("clients/{clientId:int}/sections")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> Sections(int clientId, CancellationToken ct)
    {
        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        var (managed, note) = await clientStore.GetSecurityManagementAsync(clientId, ct);
        var resolvedNote = string.IsNullOrWhiteSpace(note) ? WafConstants.SecurityManagedDefaultNote : note!;
        var sections = await recommendations.GetSectionsAsync(clientId, ct);
        return Ok(sections.Select(x =>
        {
            // El pilar de seguridad gestionado externamente conserva la tarjeta (para el score) pero
            // se neutraliza a cero y se marca, para que la UI muestre la nota en vez del conteo.
            var isSecMgmt = managed && x.SectionNum == WafConstants.SecurityPillar;
            return new
            {
                section_num = x.SectionNum,
                section_name = PillarSectionNames.GetValueOrDefault(x.SectionNum, x.SectionName),
                total_recs = isSecMgmt ? 0 : x.TotalRecs,
                total_resources = isSecMgmt ? 0 : x.TotalResources,
                avg_progress = isSecMgmt ? 0.0 : x.AvgProgress,
                high_recs = isSecMgmt ? 0 : x.HighRecs,
                medium_recs = isSecMgmt ? 0 : x.MediumRecs,
                managed_externally = isSecMgmt,
                managed_note = isSecMgmt ? resolvedNote : (string?)null,
            };
        }));
    }

    /// <summary>GET estado de gestión externa de seguridad (Gestión de Vulnerabilidades) del cliente.</summary>
    [HttpGet("clients/{clientId:int}/security-management")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> GetSecurityManagement(int clientId, CancellationToken ct)
    {
        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        var (managed, note) = await clientStore.GetSecurityManagementAsync(clientId, ct);
        return Ok(new
        {
            managed_externally = managed,
            note = string.IsNullOrWhiteSpace(note) ? WafConstants.SecurityManagedDefaultNote : note,
        });
    }

    /// <summary>PUT estado de gestión externa de seguridad del cliente.</summary>
    [HttpPut("clients/{clientId:int}/security-management")]
    [RequireModule(Modules.Waf, ModuleAccess.Edit)]
    public async Task<IActionResult> SetSecurityManagement(int clientId, [FromBody] WafSecurityManagementRequest payload, CancellationToken ct)
    {
        if (payload is null) return BadRequest(new { detail = "Cuerpo requerido." });
        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        var note = payload.Note is { Length: > 1000 } long_ ? long_[..1000] : payload.Note;
        await clientStore.SetSecurityManagementAsync(clientId, payload.ManagedExternally, note, ct);
        return Ok(new { message = "Configuración guardada", client_id = clientId, managed_externally = payload.ManagedExternally });
    }

    /// <summary>POST /waf/translate — traduce textos WAF es→en en vivo (sin persistir).</summary>
    [HttpPost("translate")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> Translate([FromBody] WafTranslateRequest payload, CancellationToken ct)
    {
        if (payload?.Items is null || payload.Items.Count == 0)
            return BadRequest(new { detail = "No hay textos para traducir." });
        if (!string.Equals(payload.Target, "en", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { detail = "Solo se admite target=en." });
        if (payload.Items.Count > 500)
            return BadRequest(new { detail = "Demasiados textos en una sola solicitud (max 500)." });
        if (payload.Items.Sum(i => (i.Text ?? "").Length) > 200_000)
            return BadRequest(new { detail = "El texto total excede el limite permitido." });
        if (!translation.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { detail = "Traduccion no disponible: Azure OpenAI no esta configurado." });

        try
        {
            var input = payload.Items.Select(i => new WafTranslationItem(i.Key, i.Text ?? "")).ToList();
            var result = await translation.TranslateAsync("en", input, ct);
            return Ok(new { items = result.Select(t => new { key = t.Key, text = t.Text }) });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WAF translate failed");
            return Problem(statusCode: StatusCodes.Status502BadGateway, detail: "La traduccion fallo. Intenta de nuevo.");
        }
    }

    /// <summary>GET advisor-score — último snapshot guardado (no consulta ARM). Port de waf_client_advisor_score.</summary>
    [HttpGet("clients/{clientId:int}/advisor-score")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> AdvisorScore(int clientId, [FromQuery] bool detail = false, CancellationToken ct = default)
    {
        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        var snapshot = await advisorScoreStore.LoadLatestSnapshotAsync(clientId, includeBreakdown: detail, ct);
        return Ok(snapshot is null ? MissingSnapshotResponse(clientId) : SnapshotToResponse(snapshot, detail));
    }

    /// <summary>GET advisor-score/history — serie histórica del Advisor Score (global + pilares).</summary>
    [HttpGet("clients/{clientId:int}/advisor-score/history")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> AdvisorScoreHistory(
        int clientId, [FromQuery] string granularity = "month", CancellationToken ct = default)
    {
        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        var gran = ScoreHistory.GranularityChar(granularity);
        var points = await advisorScoreStore.LoadHistoryAsync(clientId, gran, ct);
        return Ok(ScoreHistory.BuildResponse(ScoreHistory.GranularityName(gran), points));
    }

    /// <summary>GET cost-reference — costo referencial del pilar 5 reutilizando el módulo de costos. Port de waf_cost_reference.</summary>
    [HttpGet("clients/{clientId:int}/cost-reference")]
    [RequireModule(Modules.WafCost)]
    public async Task<IActionResult> CostReference(int clientId, CancellationToken ct)
    {
        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        // Analisis del cliente con mayor cobertura de costos ya calculados.
        int? analysisId = null;
        string? analysisName = null;
        await using (var pick = conn.CreateCommand())
        {
            pick.CommandText = """
                SELECT TOP 1 cr.analysis_id, ca.analysis_name, COUNT(*) AS priced
                FROM dbo.cost_results cr
                INNER JOIN dbo.cost_analysis ca ON ca.analysis_id = cr.analysis_id
                WHERE ca.client_id = @cid
                GROUP BY cr.analysis_id, ca.analysis_name
                ORDER BY COUNT(*) DESC
                """;
            pick.Parameters.Add(new SqlParameter("@cid", clientId));
            await using var pr = await pick.ExecuteReaderAsync(ct);
            if (await pr.ReadAsync(ct))
            {
                analysisId = pr.GetInt32(0);
                analysisName = pr.IsDBNull(1) ? null : pr.GetString(1);
            }
        }

        if (analysisId is null)
        {
            return Ok(new
            {
                client_id = clientId,
                has_cost_data = false,
                disclaimer = CostReferenceDisclaimer,
                message = "Este cliente aun no tiene costos calculados. Ejecuta el calculo en el modulo de costos para obtener la estimacion referencial.",
                totals = new { payg_monthly = 0, ri_1y_monthly = 0, ri_3y_monthly = 0, resources_total = 0, resources_matched = 0, resources_priced = 0 },
                items = Array.Empty<object>(),
            });
        }

        var items = new List<object>();
        double totPayg = 0, totRi1 = 0, totRi3 = 0;
        int totRes = 0, totMatched = 0, totPriced = 0;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    k.canonical_id, r.matrix_code, k.review_scope_es,
                    r.business_impact, r.impact_number,
                    COUNT(DISTINCT f.finding_id) AS resources_total,
                    COUNT(DISTINCT CASE WHEN ar.resource_id IS NOT NULL THEN f.finding_id END) AS resources_matched,
                    COUNT(DISTINCT CASE WHEN cr.payg_monthly IS NOT NULL THEN f.finding_id END) AS resources_priced,
                    SUM(COALESCE(cr.payg_monthly, 0)) AS payg_monthly,
                    SUM(COALESCE(cr.ri_1y_monthly, 0)) AS ri_1y_monthly,
                    SUM(COALESCE(cr.ri_3y_monthly, 0)) AS ri_3y_monthly
                FROM dbo.waf_recommendation r
                INNER JOIN dbo.waf_recommendation_canonical k ON k.canonical_id = r.canonical_id
                INNER JOIN dbo.waf_resource_finding f
                    ON f.recommendation_id = r.recommendation_id AND f.status = 'active'
                OUTER APPLY (
                    SELECT TOP 1 candidate.resource_id
                    FROM dbo.azure_resources candidate
                    WHERE candidate.analysis_id = @analysisId
                      AND candidate.is_active = 1
                      AND (
                        (NULLIF(f.azure_resource_id, '') IS NOT NULL
                         AND candidate.azure_resource_id = f.azure_resource_id)
                        OR
                        (NULLIF(f.azure_resource_id, '') IS NULL
                         AND candidate.resource_name = f.resource_name
                         AND candidate.subscription_id = f.subscription_id)
                      )
                    ORDER BY CASE
                        WHEN NULLIF(f.azure_resource_id, '') IS NOT NULL
                         AND candidate.azure_resource_id = f.azure_resource_id
                        THEN 0 ELSE 1
                    END
                ) ar
                LEFT JOIN dbo.cost_results cr ON cr.resource_id = ar.resource_id AND cr.analysis_id = @analysisId
                WHERE r.client_id = @cid
                  AND r.is_active = 1
                  AND COALESCE(r.is_dismissed, 0) = 0
                  AND k.pillar_number = 5
                GROUP BY k.canonical_id, r.matrix_code, k.review_scope_es, r.business_impact, r.impact_number
                ORDER BY SUM(COALESCE(cr.payg_monthly, 0)) DESC
                """;
            cmd.Parameters.Add(new SqlParameter("@analysisId", analysisId.Value));
            cmd.Parameters.Add(new SqlParameter("@cid", clientId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var payg = SafeDouble(r, 8);
                var ri1 = SafeDouble(r, 9);
                var ri3 = SafeDouble(r, 10);
                var resTotal = r.GetInt32(5);
                var resMatched = r.GetInt32(6);
                var resPriced = r.GetInt32(7);
                totPayg += payg; totRi1 += ri1; totRi3 += ri3;
                totRes += resTotal; totMatched += resMatched; totPriced += resPriced;
                items.Add(new
                {
                    canonical_id = r.GetInt32(0),
                    matrix_code = r.IsDBNull(1) ? null : r.GetString(1),
                    review_scope_es = r.IsDBNull(2) ? null : r.GetString(2),
                    business_impact = r.IsDBNull(3) ? null : r.GetString(3),
                    resources_total = resTotal,
                    resources_matched = resMatched,
                    resources_priced = resPriced,
                    payg_monthly = Math.Round(payg, 2),
                    ri_1y_monthly = Math.Round(ri1, 2),
                    ri_3y_monthly = Math.Round(ri3, 2),
                });
            }
        }

        return Ok(new
        {
            client_id = clientId,
            has_cost_data = true,
            analysis_id = analysisId.Value,
            analysis_name = analysisName,
            disclaimer = CostReferenceDisclaimer,
            totals = new
            {
                payg_monthly = Math.Round(totPayg, 2),
                ri_1y_monthly = Math.Round(totRi1, 2),
                ri_3y_monthly = Math.Round(totRi3, 2),
                resources_total = totRes,
                resources_matched = totMatched,
                resources_priced = totPriced,
            },
            items,
        });
    }

    /// <summary>GET recommendations. Port de list_waf_recommendations.</summary>
    [HttpGet("clients/{clientId:int}/recommendations")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> ListRecommendations(
        int clientId, [FromQuery] int? pillar, [FromQuery(Name = "active_only")] bool activeOnly = true, CancellationToken ct = default)
    {
        if (pillar is not null && pillar is < 1 or > 5)
            return BadRequest(new { detail = "Pillar must be between 1 and 5" });

        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        // El listado necesita campos de la canónica (pilar/scope/benefit) y de tracking
        // (completion/fecha/esfuerzo) que el modelo WafRecommendation no trae; se consulta el join
        // directamente (port 1:1 del SELECT de list_waf_recommendations).
        var where = new List<string> { "r.client_id = @cid" };
        var (secManaged, _) = await clientStore.GetSecurityManagementAsync(clientId, ct);
        if (secManaged) where.Add($"c.pillar_number <> {WafConstants.SecurityPillar}");
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        if (activeOnly)
        {
            where.Add("r.is_active = 1");
            where.Add("COALESCE(r.is_dismissed, 0) = 0");
        }
        if (pillar is not null)
        {
            where.Add("c.pillar_number = @pillar");
            cmd.Parameters.Add(new SqlParameter("@pillar", (byte)pillar.Value));
        }
        cmd.Parameters.Add(new SqlParameter("@userKey", (object?)Email ?? ""));
        cmd.CommandText = $"""
            SELECT
                r.recommendation_id, r.canonical_id, r.matrix_code,
                c.pillar_number, c.review_scope_es, c.benefit_es,
                r.business_impact, r.resource_count, r.is_active,
                COALESCE(t.completion_pct, 0) AS completion_pct,
                t.remediation_start_date, t.remediation_end_date, t.projected_bit_effort,
                r.first_seen_at, r.last_seen_at,
                CASE WHEN r.first_seen_at > (SELECT baseline_at FROM dbo.waf_feature_baseline WHERE feature = 'new_badge')
                          AND rr.read_at IS NULL THEN 1 ELSE 0 END AS is_new,
                r.source
            FROM dbo.waf_recommendation r
            INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = r.canonical_id
            LEFT JOIN dbo.waf_recommendation_tracking t
                ON t.client_id = r.client_id AND t.canonical_id = r.canonical_id
            LEFT JOIN dbo.waf_recommendation_read rr
                ON rr.client_id = r.client_id AND rr.canonical_id = r.canonical_id AND rr.user_key = @userKey
            WHERE {string.Join(" AND ", where)}
            ORDER BY c.pillar_number, r.impact_number, c.review_scope_es
            """;

        var rows = new List<object>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            rows.Add(new
            {
                recommendation_id = rd.GetInt32(0),
                canonical_id = rd.GetInt32(1),
                matrix_code = rd.IsDBNull(2) ? null : rd.GetString(2),
                pillar_number = rd.GetByte(3),
                review_scope_es = rd.IsDBNull(4) ? null : rd.GetString(4),
                benefit_es = rd.IsDBNull(5) ? null : rd.GetString(5),
                business_impact = rd.IsDBNull(6) ? null : rd.GetString(6),
                resource_count = rd.GetInt32(7),
                is_active = rd.GetBoolean(8),
                completion_pct = rd.GetInt32(9),
                remediation_start_date = rd.IsDBNull(10) ? null : rd.GetDateTime(10).ToString("yyyy-MM-dd"),
                remediation_end_date = rd.IsDBNull(11) ? null : rd.GetDateTime(11).ToString("yyyy-MM-dd"),
                projected_bit_effort = rd.IsDBNull(12) ? null : rd.GetString(12),
                first_seen_at = rd.GetDateTime(13),
                last_seen_at = rd.GetDateTime(14),
                is_new = rd.GetInt32(15) == 1,
                source = rd.IsDBNull(16) ? null : rd.GetString(16),
            });
        }
        return Ok(rows);
    }

    /// <summary>GET ingestion-runs. Port de list_waf_ingestion_runs (marca corridas colgadas).</summary>
    [HttpGet("clients/{clientId:int}/ingestion-runs")]
    [RequireModule(Modules.WafIngestions)]
    public async Task<IActionResult> IngestionRuns(int clientId, CancellationToken ct)
    {
        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        await ingestionStore.MarkStaleRunsFailedAsync(ct: ct);

        var runs = new List<object>();
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, source_file_name, status, rows_total, rows_processed,
                   new_recommendations, new_findings, resolved_findings,
                   started_at, completed_at, created_by, error_message, subscription_results
            FROM dbo.waf_ingestion_run
            WHERE client_id = @cid
            ORDER BY started_at DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            runs.Add(new
            {
                run_id = r.GetInt32(0),
                source_file_name = r.IsDBNull(1) ? null : r.GetString(1),
                status = r.IsDBNull(2) ? null : r.GetString(2),
                rows_total = r.IsDBNull(3) ? (int?)null : r.GetInt32(3),
                rows_processed = r.IsDBNull(4) ? (int?)null : r.GetInt32(4),
                new_recommendations = r.IsDBNull(5) ? (int?)null : r.GetInt32(5),
                new_findings = r.IsDBNull(6) ? (int?)null : r.GetInt32(6),
                resolved_findings = r.IsDBNull(7) ? (int?)null : r.GetInt32(7),
                started_at = r.GetDateTime(8),
                completed_at = r.IsDBNull(9) ? (DateTime?)null : r.GetDateTime(9),
                created_by = r.IsDBNull(10) ? null : r.GetString(10),
                error_message = r.IsDBNull(11) ? null : r.GetString(11),
                subscription_results = r.IsDBNull(12)
                    ? (object?)null
                    : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(r.GetString(12)),
            });
        }
        return Ok(runs);
    }

    /// <summary>GET recommendation resources (findings). Port de list_waf_recommendation_resources.</summary>
    [HttpGet("clients/{clientId:int}/recommendations/{canonicalId:int}/resources")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> ListResources(int clientId, int canonicalId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        await WafSchema.EnsureWafSchemaAsync(factory, ct);

        var rows = await recommendations.ListResourcesAsync(clientId, canonicalId, ct);
        return Ok(rows.Select(f => new
        {
            finding_id = f.FindingId,
            resource_name = f.ResourceName,
            resource_type = f.ResourceType,
            resource_group = f.ResourceGroup,
            subscription_id = f.SubscriptionId,
            subscription_name = f.SubscriptionName,
            azure_resource_id = f.AzureResourceId,
            business_impact = f.BusinessImpact,
            additional_info = f.AdditionalInfo,
            status = f.Status,
            first_seen_at = f.FirstSeenAt,
            last_seen_at = f.LastSeenAt,
        }));
    }

    /// <summary>GET recommendation detail. Port de get_waf_recommendation.</summary>
    [HttpGet("clients/{clientId:int}/recommendations/{canonicalId:int}")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> GetRecommendation(int clientId, int canonicalId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        await WafSchema.EnsureWafSchemaAsync(factory, ct);

        var rec = await recommendations.GetAsync(clientId, canonicalId, ct);
        if (rec is null) return NotFound(new { detail = "Recommendation not found" });

        var canonical = await catalog.GetCanonicalAsync(canonicalId, ct);
        var tr = await tracking.LoadAsync(clientId, canonicalId, ct);

        return Ok(new
        {
            recommendation_id = rec.RecommendationId,
            canonical_id = rec.CanonicalId,
            matrix_code = rec.MatrixCode,
            pillar_number = canonical?.PillarNumber,
            review_scope_es = canonical?.ReviewScopeEs,
            benefit_es = canonical?.BenefitEs,
            client_action_es = canonical?.ClientActionEs,
            bit_action_es = canonical?.BitActionEs,
            business_impact = rec.BusinessImpact,
            impact_number = rec.ImpactNumber,
            resource_count = rec.ResourceCount,
            completion_pct = tr.CompletionPct,
            remediation_start_date = tr.RemediationStartDate?.ToString("yyyy-MM-dd"),
            remediation_end_date = tr.RemediationEndDate?.ToString("yyyy-MM-dd"),
            projected_bit_effort = tr.ProjectedBitEffort,
            execution_log = tr.ExecutionLog,
            priority_override = tr.PriorityOverride,
            internal_notes = tr.InternalNotes,
            first_seen_at = rec.FirstSeenAt,
            last_seen_at = rec.LastSeenAt,
        });
    }

    /// <summary>GET comments. Port de list_waf_comments.</summary>
    [HttpGet("clients/{clientId:int}/recommendations/{canonicalId:int}/comments")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> ListComments(int clientId, int canonicalId, CancellationToken ct)
    {
        var guard = await GuardRecommendationAsync(clientId, canonicalId, ct);
        if (guard is not null) return guard;

        var comments = await tracking.GetCommentsAsync(clientId, canonicalId, ct: ct);
        return Ok(comments.Select(c => new
        {
            comment_id = c.CommentId,
            user_display = c.UserDisplay,
            comment_text = c.CommentText,
            created_at = c.CreatedAt,
        }));
    }

    /// <summary>GET history. Port de list_waf_history.</summary>
    [HttpGet("clients/{clientId:int}/recommendations/{canonicalId:int}/history")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> ListHistory(int clientId, int canonicalId, CancellationToken ct)
    {
        var guard = await GuardRecommendationAsync(clientId, canonicalId, ct);
        if (guard is not null) return guard;

        var history = await tracking.GetHistoryAsync(clientId, canonicalId, ct: ct);
        return Ok(history.Select(h => new
        {
            history_id = h.HistoryId,
            field_changed = h.FieldChanged,
            old_value = h.OldValue,
            new_value = h.NewValue,
            changed_by = h.ChangedBy,
            changed_at = h.ChangedAt,
        }));
    }

    // ==================== POR CLIENTE — MODIFICACIÓN ====================

    /// <summary>POST advisor-sync — crea una corrida persistida y la procesa fuera de la petición HTTP.</summary>
    [HttpPost("clients/{clientId:int}/advisor-sync")]
    [RequireModule(Modules.Waf, ModuleAccess.Edit)]
    public async Task<IActionResult> AdvisorSync(int clientId, [FromBody] WafAdvisorSyncRequest payload, CancellationToken ct)
    {
        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        // Valida que las suscripciones pedidas existan y esten gestionadas (port de _resolve_waf_subscription_groups).
        var groups = await advisorScoreStore.LoadClientSubscriptionGroupsAsync(clientId, ct);
        var available = new HashSet<string>(
            groups.SelectMany(g => g.Value.Subscriptions.Keys), StringComparer.OrdinalIgnoreCase);
        if (available.Count == 0)
            return BadRequest(new { detail = "El cliente no tiene suscripciones Azure activas para consultar Advisor." });
        if (payload.Subscriptions is { Count: > 0 })
        {
            var unknown = payload.Subscriptions.Where(s => !available.Contains(s)).ToList();
            if (unknown.Count > 0)
                return BadRequest(new { detail = $"Suscripciones no registradas o inactivas para este cliente: [{string.Join(", ", unknown)}]" });
        }

        var selected = payload.Subscriptions is { Count: > 0 }
            ? payload.Subscriptions
            : available.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var (job, created) = await advisorSyncJobs.CreateOrGetActiveAsync(
            clientId, selected, Email, payload.TimeoutSecondsPerSubscription, ct);
        if (created)
            advisorSyncQueue.Enqueue(new WafAdvisorSyncJob(
                job.JobId, clientId, selected, Email, payload.TimeoutSecondsPerSubscription));

        return StatusCode(StatusCodes.Status202Accepted, AdvisorSyncJobResponse(job, created));
    }

    /// <summary>GET de una corrida de Advisor para polling del loader global.</summary>
    [HttpGet("clients/{clientId:int}/advisor-sync/{jobId:int}")]
    public async Task<IActionResult> AdvisorSyncStatus(int clientId, int jobId, CancellationToken ct)
    {
        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;
        var job = await advisorSyncJobs.GetAsync(clientId, jobId, ct);
        return job is null ? NotFound(new { detail = "Corrida de Advisor no encontrada." }) : Ok(AdvisorSyncJobResponse(job, false));
    }

    /// <summary>GET de la corrida activa; permite recuperar el bloqueo después de recargar.</summary>
    [HttpGet("clients/{clientId:int}/advisor-sync/active")]
    public async Task<IActionResult> ActiveAdvisorSync(int clientId, CancellationToken ct)
    {
        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;
        var job = await advisorSyncJobs.GetActiveAsync(clientId, ct);
        return Ok(job is null
            ? (object)new { active = false }
            : AdvisorSyncJobResponse(job, false));
    }

    /// <summary>POST consolidate-duplicates (admin/consultor). Port de consolidate_waf_duplicates.</summary>
    [HttpPost("clients/{clientId:int}/consolidate-duplicates")]
    [RequireModule(Modules.Waf, ModuleAccess.Edit)]
    public async Task<IActionResult> ConsolidateDuplicates(int clientId, [FromQuery(Name = "use_ai")] bool useAi = true, CancellationToken ct = default)
    {
        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        try
        {
            var result = await orchestrator.ConsolidateAsync(clientId, useAi, Email, ct);
            return Ok(new
            {
                message = $"Consolidacion completada: {result.Merged} duplicado(s) fusionado(s).",
                client_id = clientId,
                merged = result.Merged,
                ai_calls = result.AiCalls,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WAF consolidation failed client_id={Cid}", clientId);
            return Problem(statusCode: 500, detail: $"WAF consolidation failed: {ex.GetType().Name}");
        }
    }

    /// <summary>DELETE recommendation — descarta (soft delete). Port de dismiss_waf_recommendation.</summary>
    [HttpDelete("clients/{clientId:int}/recommendations/{canonicalId:int}")]
    [RequireModule(Modules.Waf, ModuleAccess.Edit)]
    public async Task<IActionResult> Dismiss(int clientId, int canonicalId, [FromBody] WafRecommendationDismiss? payload = null, CancellationToken ct = default)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var reason = string.IsNullOrWhiteSpace(payload?.Reason) ? "Descartada por decision del cliente" : payload!.Reason!;
        try
        {
            await recommendations.DismissAsync(clientId, canonicalId, reason, Email, ct);
            return Ok(new { message = "Recomendacion descartada", client_id = clientId, canonical_id = canonicalId });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { detail = "Recommendation not found" });
        }
    }

    /// <summary>POST read — marca la recomendación como vista por el usuario actual (idempotente).</summary>
    [HttpPost("clients/{clientId:int}/recommendations/{canonicalId:int}/read")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> MarkRead(int clientId, int canonicalId, CancellationToken ct = default)
    {
        var guard = await GuardRecommendationAsync(clientId, canonicalId, ct);
        if (guard is not null) return guard;
        await recommendations.MarkReadAsync(clientId, canonicalId, Email ?? "", ct);
        return NoContent();
    }

    /// <summary>PUT tracking. Port de update_waf_tracking.</summary>
    [HttpPut("clients/{clientId:int}/recommendations/{canonicalId:int}/tracking")]
    [RequireModule(Modules.Waf, ModuleAccess.Edit)]
    public async Task<IActionResult> UpdateTracking(int clientId, int canonicalId, [FromBody] WafTrackingUpdate payload, CancellationToken ct)
    {
        if (!AnyTrackingFieldSet(payload))
            return BadRequest(new { detail = "No tracking fields were provided" });

        var guard = await GuardRecommendationAsync(clientId, canonicalId, ct);
        if (guard is not null) return guard;

        await tracking.ApplyUpdateAsync(clientId, canonicalId, payload, Email, ct);
        return Ok(new { message = "Seguimiento actualizado", client_id = clientId, canonical_id = canonicalId });
    }

    /// <summary>POST comment. Port de create_waf_comment.</summary>
    [HttpPost("clients/{clientId:int}/recommendations/{canonicalId:int}/comments")]
    [RequireModule(Modules.Waf, ModuleAccess.Edit)]
    public async Task<IActionResult> CreateComment(int clientId, int canonicalId, [FromBody] WafCommentCreate payload, CancellationToken ct)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.CommentText))
            return BadRequest(new { detail = "comment_text es requerido" });

        var guard = await GuardRecommendationAsync(clientId, canonicalId, ct);
        if (guard is not null) return guard;

        var userDisplay = UserName ?? Email ?? "Usuario BIT";
        var commentId = await tracking.AddCommentAsync(clientId, canonicalId, payload.CommentText.Trim(), userDisplay, ct);
        return Ok(new { message = "Comentario registrado", comment_id = commentId });
    }

    // ==================== EXCEL ====================

    /// <summary>GET export-excel. Port de export_waf_client_excel.</summary>
    [HttpGet("clients/{clientId:int}/export-excel")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> ExportExcel(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        await WafSchema.EnsureWafSchemaAsync(factory, ct);

        var clientName = await ClientNameAsync(clientId, ct);
        if (clientName is null) return NotFound(new { detail = "Client not found" });

        var (secManaged, _) = await clientStore.GetSecurityManagementAsync(clientId, ct);
        var rows = await BuildExportRowsAsync(clientId, secManaged, ct);
        var bytes = await excelExporter.ExportAsync(clientId, rows, ct);

        var safeName = new string((clientName ?? "").Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        var fileName = $"Matriz-Optimizacion-Mejoras-{(safeName.Length > 0 ? safeName : clientId.ToString())}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    /// <summary>GET excel-import/status. Port de waf_excel_import_status.</summary>
    [HttpGet("clients/{clientId:int}/excel-import/status")]
    [RequireModule(Modules.Waf)]
    public async Task<IActionResult> ExcelImportStatus(int clientId, CancellationToken ct)
    {
        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        return Ok(new
        {
            status = "ready",
            client_id = clientId,
            supports_preview = true,
            supports_apply = true,
            ai_configured = curator.GetConfigStatus().Configured,
        });
    }

    /// <summary>POST excel-import/preview. Port de preview_waf_excel_import.</summary>
    [HttpPost("clients/{clientId:int}/excel-import/preview")]
    [RequireModule(Modules.Waf, ModuleAccess.Edit)]
    public async Task<IActionResult> ExcelImportPreview(
        int clientId, IFormFile file, [FromQuery(Name = "use_ai")] bool useAi = true, CancellationToken ct = default)
    {
        var fileName = SafeFileName(file?.FileName);
        if (!fileName.ToLowerInvariant().EndsWith(".xlsx"))
            return BadRequest(new { detail = "El archivo debe ser una matriz Excel .xlsx." });
        if (file is null || file.Length == 0)
            return BadRequest(new { detail = "El archivo Excel esta vacio." });

        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        byte[] content;
        using (var ms = new MemoryStream()) { await file.CopyToAsync(ms, ct); content = ms.ToArray(); }
        if (content.Length == 0) return BadRequest(new { detail = "El archivo Excel esta vacio." });

        try
        {
            var (rows, metrics) = excelImporter.Parse(content);
            var aiConfigured = useAi && curator.GetConfigStatus().Configured;
            var preview = await excelImporter.PreviewAsync(clientId, rows, useAi, maxAiCalls: 40, ct);
            var matched = preview.Count(p => p.Status == "matched");
            var needsReview = preview.Count - matched;
            return Ok(new
            {
                file_name = fileName,
                client_id = clientId,
                rows_total = preview.Count,
                rows_matched = matched,
                rows_needs_review = needsReview,
                ai_enabled = aiConfigured,
                metrics = new { sheet = metrics.Sheet, rows_total = metrics.RowsTotal, rows_with_warnings = metrics.RowsWithWarnings },
                rows = preview.Select(ToPreviewDto),
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { detail = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WAF Excel preview failed client_id={Cid}", clientId);
            return Problem(statusCode: 500, detail: $"WAF Excel preview failed: {ex.GetType().Name}");
        }
    }

    /// <summary>POST excel-import/apply. Port de apply_waf_excel_import.</summary>
    [HttpPost("clients/{clientId:int}/excel-import/apply")]
    [RequireModule(Modules.Waf, ModuleAccess.Edit)]
    public async Task<IActionResult> ExcelImportApply(int clientId, [FromBody] WafExcelApplyRequest payload, CancellationToken ct)
    {
        if (payload?.Rows is null || payload.Rows.Count == 0)
            return BadRequest(new { detail = "No hay filas aprobadas para aplicar." });

        // Validacion cruzada de cada item (port del model_validator del Pydantic).
        foreach (var item in payload.Rows)
        {
            if (item.Action == "update" && (item.CanonicalId is null or <= 0))
                return BadRequest(new { detail = "canonical_id es requerido para actualizar una recomendacion existente." });
            if (item.Action == "create" && (item.PillarNumber is null || string.IsNullOrWhiteSpace(item.Title)))
                return BadRequest(new { detail = "pillar_number y title son requeridos para crear una recomendacion." });
        }

        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        try
        {
            var items = payload.Rows.Select(ToDomainApplyItem).ToList();
            var result = await excelImporter.ApplyAsync(clientId, items, Email, ct);
            return Ok(new
            {
                message = "Importacion Excel aplicada",
                client_id = clientId,
                rows_applied = result.RowsApplied,
                rows_created = result.RowsCreated,
                rows_skipped = result.RowsSkipped,
                changed_fields = result.ChangedFields,
                errors = result.Errors.Select(e => new { row_number = e.RowNumber, detail = e.Detail }),
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WAF Excel apply failed client_id={Cid}", clientId);
            return Problem(statusCode: 500, detail: $"WAF Excel apply failed: {ex.GetType().Name}");
        }
    }

    /// <summary>POST ingestions — carga CSV de Advisor (síncrona). Port de upload_waf_ingestion.</summary>
    [HttpPost("clients/{clientId:int}/ingestions")]
    [RequireModule(Modules.WafIngestions, ModuleAccess.Edit)]
    public async Task<IActionResult> UploadIngestion(int clientId, IFormFile file, CancellationToken ct)
    {
        var fileName = SafeFileName(file?.FileName);
        if (!fileName.ToLowerInvariant().EndsWith(".csv"))
            return BadRequest(new { detail = "El archivo debe ser un CSV exportado desde Azure Advisor." });

        var guard = await GuardAsync(clientId, ct);
        if (guard is not null) return guard;

        if (file is null || file.Length == 0) return BadRequest(new { detail = "El archivo CSV esta vacio." });
        byte[] content;
        using (var ms = new MemoryStream()) { await file.CopyToAsync(ms, ct); content = ms.ToArray(); }
        if (content.Length == 0) return BadRequest(new { detail = "El archivo CSV esta vacio." });

        try
        {
            var (result, merged) = await orchestrator.RunCsvAsync(clientId, fileName, content, Email, ct);
            return Ok(new
            {
                run_id = result.RunId,
                status = result.Warnings.Count > 0 ? "completed_with_errors" : "completed",
                rows_total = result.Metrics.RowsTotal,
                rows_processed = result.Metrics.RowsProcessed,
                rows_skipped = result.Metrics.RowsSkipped,
                rows_duplicate_skipped = result.Metrics.RowsDuplicateSkipped,
                new_recommendations = result.NewRecommendations,
                new_findings = result.NewFindings,
                resolved_findings = result.ResolvedFindings,
                merged_duplicates = merged,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { detail = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WAF ingestion failed client_id={Cid}", clientId);
            return Problem(statusCode: 500, detail: $"WAF ingestion failed: {ex.GetType().Name}");
        }
    }

    // ==================== HELPERS ====================

    /// <summary>Asserta acceso y existencia del cliente (assert_client_access + _client_exists). null si OK.</summary>
    private async Task<IActionResult?> GuardAsync(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        await WafSchema.EnsureWafSchemaAsync(factory, ct);
        if (!await ClientExistsAsync(clientId, ct)) return NotFound(new { detail = "Client not found" });
        return null;
    }

    /// <summary>Asserta acceso + existencia de la recomendación (assert + _recommendation_exists). null si OK.</summary>
    private async Task<IActionResult?> GuardRecommendationAsync(int clientId, int canonicalId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        await WafSchema.EnsureWafSchemaAsync(factory, ct);
        if (!await RecommendationExistsAsync(clientId, canonicalId, ct))
            return NotFound(new { detail = "Recommendation not found" });
        return null;
    }

    private async Task<bool> ClientExistsAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dbo.clients WHERE client_id = @cid";
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private async Task<string?> ClientNameAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_name FROM dbo.clients WHERE client_id = @cid";
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is null or DBNull ? null : Convert.ToString(r);
    }

    private async Task<bool> RecommendationExistsAsync(int clientId, int canonicalId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM dbo.waf_recommendation
            WHERE client_id = @cid AND canonical_id = @kid AND COALESCE(is_dismissed, 0) = 0
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@kid", canonicalId));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Arma las filas de exportación (canónica + tracking + recursos activos). Port del SELECT de export_waf_client_excel.</summary>
    private async Task<IReadOnlyList<WafExportRow>> BuildExportRowsAsync(int clientId, bool excludeSecurity, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = new List<(int RecId, WafExportRow Row)>();
        // Si la seguridad se gestiona externamente, la sección Seguridad queda vacía (placeholders):
        // se filtran las filas, sin reestructurar la plantilla del Excel.
        var secFilter = excludeSecurity ? $" AND c.pillar_number <> {WafConstants.SecurityPillar}" : "";
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT
                    r.recommendation_id, r.canonical_id, r.matrix_code,
                    c.pillar_number, c.review_scope_es, c.benefit_es,
                    c.client_action_es, c.bit_action_es,
                    r.business_impact, r.impact_number, r.resource_count,
                    COALESCE(t.completion_pct, 0) AS completion_pct,
                    t.remediation_start_date, t.projected_bit_effort,
                    t.execution_log, t.priority_override, r.last_seen_at
                FROM dbo.waf_recommendation r
                INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = r.canonical_id
                LEFT JOIN dbo.waf_recommendation_tracking t
                    ON t.client_id = r.client_id AND t.canonical_id = r.canonical_id
                WHERE r.client_id = @cid
                  AND r.is_active = 1
                  AND COALESCE(r.is_dismissed, 0) = 0{secFilter}
                ORDER BY c.pillar_number, r.impact_number, c.review_scope_es
                """;
            cmd.Parameters.Add(new SqlParameter("@cid", clientId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var recId = r.GetInt32(0);
                rows.Add((recId, new WafExportRow(
                    CanonicalId: r.GetInt32(1),
                    PillarNumber: r.GetByte(3),
                    ReviewScopeEs: r.IsDBNull(4) ? null : r.GetString(4),
                    BenefitEs: r.IsDBNull(5) ? null : r.GetString(5),
                    ClientActionEs: r.IsDBNull(6) ? null : r.GetString(6),
                    BitActionEs: r.IsDBNull(7) ? null : r.GetString(7),
                    Resources: null,
                    ResourceCount: r.GetInt32(10),
                    CompletionPct: r.GetInt32(11),
                    LastSeenAt: r.IsDBNull(16) ? null : r.GetDateTime(16),
                    BusinessImpact: r.IsDBNull(8) ? null : r.GetString(8),
                    ImpactNumber: r.GetByte(9),
                    PriorityOverride: r.IsDBNull(15) ? null : r.GetByte(15),
                    RemediationStartDate: r.IsDBNull(12) ? null : r.GetDateTime(12),
                    ProjectedBitEffort: r.IsDBNull(13) ? null : r.GetString(13),
                    ExecutionLog: r.IsDBNull(14) ? null : r.GetString(14))));
            }
        }

        // Recursos activos por recomendación (matrix_code la asigna el exporter; aquí no se requiere).
        var result = new List<WafExportRow>(rows.Count);
        foreach (var (recId, row) in rows)
        {
            var resources = new List<string>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT resource_name FROM dbo.waf_resource_finding
                WHERE recommendation_id = @rid AND status = 'active'
                ORDER BY resource_name
                """;
            cmd.Parameters.Add(new SqlParameter("@rid", recId));
            await using var rr = await cmd.ExecuteReaderAsync(ct);
            while (await rr.ReadAsync(ct))
                if (!rr.IsDBNull(0)) resources.Add(rr.GetString(0));
            result.Add(row with { Resources = resources });
        }
        return result;
    }

    private static bool AnyTrackingFieldSet(WafTrackingUpdate p) =>
        p.CompletionPctSet || p.RemediationStartDateSet || p.RemediationEndDateSet || p.ProjectedBitEffortSet
        || p.ExecutionLogSet || p.PriorityOverrideSet || p.InternalNotesSet;

    private static double SafeDouble(SqlDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return 0;
        var v = r.GetValue(ordinal);
        return v switch
        {
            double d => d,
            decimal m => (double)m,
            float f => f,
            _ => Convert.ToDouble(v),
        };
    }

    private static string SafeFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "archivo";
        var name = Path.GetFileName(raw);
        return string.IsNullOrWhiteSpace(name) ? "archivo" : name;
    }

    private static WafExcelApplyItem ToDomainApplyItem(WafExcelApplyItemRequest x) => new(
        RowNumber: x.RowNumber,
        Action: x.Action,
        CanonicalId: x.CanonicalId,
        Approved: x.Approved,
        CompletionPct: x.CompletionPct,
        RemediationStartDate: x.RemediationStartDate,
        ExecutionLog: x.ExecutionLog,
        PillarNumber: x.PillarNumber,
        Title: x.Title,
        ReviewScope: x.ReviewScope,
        Benefit: x.Benefit,
        Actions: x.Actions,
        Impact: x.Impact,
        ProjectedBitEffort: x.ProjectedBitEffort,
        Resources: x.Resources);

    private static object ToPreviewDto(WafExcelPreviewItem item) => new
    {
        row = new
        {
            row_number = item.Row.RowNumber,
            pillar_number = item.Row.PillarNumber,
            excel_code = item.Row.ExcelCode,
            title = item.Row.Title,
            raw_scope = item.Row.RawScope,
            review_date = item.Row.ReviewDate,
            optimization_status = item.Row.OptimizationStatus,
            resource_summary = item.Row.ResourceSummary,
            benefit = item.Row.Benefit,
            actions = item.Row.Actions,
            impact = item.Row.Impact,
            priority = item.Row.Priority,
            remediation_start_date = item.Row.RemediationStartDate,
            projected_bit_effort = item.Row.ProjectedBitEffort,
            completion_pct = item.Row.CompletionPct,
            execution_log = item.Row.ExecutionLog,
            resources = item.Row.Resources,
            warnings = item.Row.Warnings,
        },
        status = item.Status,
        can_create = item.CanCreate,
        match_source = item.MatchSource,
        confidence = item.Confidence,
        reason = item.Reason,
        suggested_match = item.SuggestedMatch is null ? null : new
        {
            canonical_id = item.SuggestedMatch.CanonicalId,
            matrix_code = item.SuggestedMatch.MatrixCode,
            pillar_number = item.SuggestedMatch.PillarNumber,
            review_scope_es = item.SuggestedMatch.ReviewScopeEs,
            advisor_name = item.SuggestedMatch.AdvisorName,
            current_completion_pct = item.SuggestedMatch.CurrentCompletionPct,
            current_remediation_start_date = item.SuggestedMatch.CurrentRemediationStartDate,
            current_execution_log = item.SuggestedMatch.CurrentExecutionLog,
        },
        candidates = item.Candidates.Select(c => new
        {
            canonical_id = c.CanonicalId,
            matrix_code = c.MatrixCode,
            pillar_number = c.PillarNumber,
            review_scope_es = c.ReviewScopeEs,
            advisor_name = c.AdvisorName,
            current_completion_pct = c.CurrentCompletionPct,
            current_remediation_start_date = c.CurrentRemediationStartDate,
            current_execution_log = c.CurrentExecutionLog,
            score = c.Score,
            reason = c.Reason,
        }),
        tracking_updates = item.TrackingUpdates,
    };

    private static object SnapshotToResponse(WafAdvisorScoreSnapshot s, bool includeBreakdown)
    {
        var pillars = new Dictionary<string, decimal>();
        if (s.ScoreP1 is decimal p1) pillars["1"] = Math.Round(p1, 2);
        if (s.ScoreP2 is decimal p2) pillars["2"] = Math.Round(p2, 2);
        if (s.ScoreP3 is decimal p3) pillars["3"] = Math.Round(p3, 2);
        if (s.ScoreP4 is decimal p4) pillars["4"] = Math.Round(p4, 2);
        if (s.ScoreP5 is decimal p5) pillars["5"] = Math.Round(p5, 2);

        var stale = (DateTime.UtcNow.Date - s.SnapshotDate.ToDateTime(TimeOnly.MinValue).Date).TotalDays > 8;
        var result = new Dictionary<string, object?>
        {
            ["client_id"] = s.ClientId,
            ["has_connection"] = s.HasConnection,
            ["subscriptions_scored"] = s.SubscriptionsScored,
            ["pillars"] = pillars,
            ["warnings"] = JsonOrDefault(s.WarningsJson),
            ["status"] = string.IsNullOrEmpty(s.Status) ? "unknown" : s.Status,
            ["source"] = string.IsNullOrEmpty(s.Source) ? "scheduled" : s.Source,
            ["include_in_reports"] = s.IncludeInReports,
            ["snapshot_date"] = s.SnapshotDate.ToString("yyyy-MM-dd"),
            ["captured_at"] = s.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["stale"] = stale,
        };
        if (includeBreakdown) result["breakdown"] = JsonOrDefault(s.BreakdownJson);
        return result;
    }

    private static object MissingSnapshotResponse(int clientId) => new
    {
        client_id = clientId,
        has_connection = false,
        subscriptions_scored = 0,
        pillars = new Dictionary<string, decimal>(),
        warnings = Array.Empty<object>(),
        status = "missing",
        source = (string?)null,
        include_in_reports = false,
        snapshot_date = (string?)null,
        captured_at = (string?)null,
        stale = true,
        message = "Advisor Score pendiente de refresh programado.",
    };

    private static object AdvisorSyncJobResponse(WafAdvisorSyncJobStatus job, bool created) => new
    {
        active = job.Status is "queued" or "running",
        created,
        job_id = job.JobId,
        client_id = job.ClientId,
        status = job.Status,
        subscriptions_total = job.SubscriptionsTotal,
        subscriptions_queued = job.SubscriptionsTotal,
        subscriptions_processed = job.SubscriptionsProcessed,
        subscriptions_failed = job.SubscriptionsFailed,
        current_subscription = job.CurrentSubscription,
        run_id = job.IngestionRunId,
        new_recommendations = job.NewRecommendations,
        new_findings = job.NewFindings,
        resolved_findings = job.ResolvedFindings,
        warnings = JsonOrDefault(job.WarningsJson),
        error = job.Error,
        started_at = job.StartedAt,
        completed_at = job.CompletedAt,
    };

    private static object JsonOrDefault(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<object>();
        try { return System.Text.Json.JsonSerializer.Deserialize<object>(json) ?? Array.Empty<object>(); }
        catch { return Array.Empty<object>(); }
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
