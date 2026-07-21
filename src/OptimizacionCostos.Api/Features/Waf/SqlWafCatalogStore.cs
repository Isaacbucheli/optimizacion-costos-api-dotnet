using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Implementación SQL del catálogo de canónicas y aliases. Schema lazy: cada método de entrada
/// público abre su propia conexión y llama a WafSchema.EnsureWafSchemaAsync. Los métodos que reciben
/// (conn, tx) participan en la transacción de ingesta que abre el caller (que ya aseguró el schema).
/// SQL siempre parametrizado. Port de las canónicas de app/waf/ingestion.py + helpers de waf.py.
/// </summary>
public sealed class SqlWafCatalogStore(ISqlConnectionFactory factory) : IWafCatalogStore
{
    private const string CanonicalSelect = """
        SELECT canonical_id, advisor_name, advisor_category, pillar_number,
               review_scope_es, benefit_es, client_action_es, bit_action_es,
               is_excluded, exclusion_reason, consolidates_to_id,
               ai_review_status, ai_decision, ai_confidence, ai_possible_additional_cost,
               ai_cost_reason, ai_exclusion_reason, ai_duplicate_group_key, ai_raw_model_text,
               ai_reviewed_by, ai_reviewed_at, created_at, updated_at
        FROM dbo.waf_recommendation_canonical
        """;

    /// <summary>Columnas que UpdateCatalogAsync acepta (whitelist; los nombres nunca vienen del usuario).</summary>
    private static readonly string[] CatalogUpdatable =
    [
        "pillar_number", "review_scope_es", "benefit_es", "client_action_es", "bit_action_es",
        "is_excluded", "exclusion_reason", "ai_review_status",
    ];

    // ---------- GetOrCreateCanonical (port de _get_or_create_canonical) ----------

    public async Task<(int CanonicalId, byte Pillar, bool Created)> GetOrCreateCanonicalAsync(
        SqlConnection conn, SqlTransaction tx, AdvisorRow row,
        Func<SqlConnection, SqlTransaction, AdvisorRow, Task<int?>>? dedupResolver,
        CancellationToken ct)
    {
        // 1. Match exacto por firma (advisor_name, advisor_category) case-insensitive.
        await using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = """
                SELECT canonical_id, pillar_number
                FROM dbo.waf_recommendation_canonical
                WHERE LOWER(advisor_name) = LOWER(@name) AND LOWER(advisor_category) = LOWER(@category)
                """;
            find.Parameters.Add(new SqlParameter("@name", row.AdvisorName));
            find.Parameters.Add(new SqlParameter("@category", row.AdvisorCategory));
            await using var r = await find.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                return (r.GetInt32(0), r.GetByte(1), false);
        }

        // 2. Dedup (alias aprendido / IA): si confirma, reutiliza esa canónica.
        if (dedupResolver is not null)
        {
            var matchedId = await dedupResolver(conn, tx, row);
            if (matchedId is int mid)
            {
                await using var pcmd = conn.CreateCommand();
                pcmd.Transaction = tx;
                pcmd.CommandText = "SELECT pillar_number FROM dbo.waf_recommendation_canonical WHERE canonical_id = @id";
                pcmd.Parameters.Add(new SqlParameter("@id", mid));
                await using var pr = await pcmd.ExecuteReaderAsync(ct);
                if (await pr.ReadAsync(ct))
                    return (mid, pr.GetByte(0), false);
            }
        }

        // 3. Crea canónica nueva (ai_review_status='pending') con el pilar derivado de la categoría.
        var pillar = PillarFromCategory(row.AdvisorCategory);
        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO dbo.waf_recommendation_canonical (
                advisor_name, advisor_category, pillar_number,
                review_scope_es, benefit_es, client_action_es, bit_action_es,
                ai_review_status
            )
            OUTPUT INSERTED.canonical_id
            VALUES (@name, @category, @pillar, @scope, @benefit, @clientAction, @bitAction, 'pending')
            """;
        ins.Parameters.Add(new SqlParameter("@name", row.AdvisorName));
        ins.Parameters.Add(new SqlParameter("@category", row.AdvisorCategory));
        ins.Parameters.Add(new SqlParameter("@pillar", pillar));
        ins.Parameters.Add(new SqlParameter("@scope", row.AdvisorName));
        ins.Parameters.Add(new SqlParameter("@benefit",
            "Recomendacion detectada desde Azure Advisor. Pendiente de revision funcional."));
        ins.Parameters.Add(new SqlParameter("@clientAction",
            "Validar aplicabilidad y acordar el plan de remediacion."));
        ins.Parameters.Add(new SqlParameter("@bitAction",
            "Revisar el hallazgo, completar el contexto tecnico y proponer acciones."));
        var newId = (int)(await ins.ExecuteScalarAsync(ct))!;
        return (newId, pillar, true);
    }

    /// <summary>
    /// categoría → pilar (port de _pillar): normaliza (NFKD ascii, lower, sin espacios) y mapea con
    /// CategoryToPillar; si no, prueba con la versión solo-lower (con espacios); default 2.
    /// </summary>
    private static byte PillarFromCategory(string advisorCategory)
    {
        var noSpace = WafText.NormalizeText(advisorCategory).Replace(" ", "");
        if (WafConstants.CategoryToPillar.TryGetValue(noSpace, out var p)) return p;
        var lowered = (advisorCategory ?? "").Trim().ToLowerInvariant();
        if (WafConstants.CategoryToPillar.TryGetValue(lowered, out var p2)) return p2;
        return WafConstants.DefaultPillar;
    }

    // ---------- GetCanonical / ListCatalog ----------

    public async Task<WafCanonical?> GetCanonicalAsync(int canonicalId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{CanonicalSelect} WHERE canonical_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", canonicalId));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? MapCanonical(r) : null;
    }

    public async Task<IReadOnlyList<WafCanonical>> ListCatalogAsync(
        string? reviewStatus, bool? excluded, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        var where = new List<string>();
        await using var cmd = conn.CreateCommand();
        if (!string.IsNullOrEmpty(reviewStatus))
        {
            where.Add("COALESCE(ai_review_status, 'pending') = @status");
            cmd.Parameters.Add(new SqlParameter("@status", reviewStatus));
        }
        if (excluded is bool ex)
        {
            where.Add("is_excluded = @excluded");
            cmd.Parameters.Add(new SqlParameter("@excluded", ex ? 1 : 0));
        }
        var clause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";
        cmd.CommandText =
            $"{CanonicalSelect} {clause} ORDER BY is_excluded, pillar_number, advisor_category, advisor_name";

        var items = new List<WafCanonical>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(MapCanonical(r));
        return items;
    }

    public async Task<bool> UpdateCatalogAsync(
        int canonicalId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        var sets = new List<string>();
        var parameters = new List<SqlParameter>();
        var i = 0;
        foreach (var col in CatalogUpdatable)
        {
            if (!fields.TryGetValue(col, out var value)) continue;
            // is_excluded entra como BIT; convertir bool → 1/0.
            if (col == "is_excluded" && value is bool b) value = b ? 1 : 0;
            sets.Add($"{col} = @p{i}");
            parameters.Add(new SqlParameter($"@p{i}", value ?? DBNull.Value));
            i++;
        }
        if (sets.Count == 0) return false;
        sets.Add("updated_at = SYSUTCDATETIME()");

        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"UPDATE dbo.waf_recommendation_canonical SET {string.Join(", ", sets)} WHERE canonical_id = @id";
        cmd.Parameters.AddRange(parameters.ToArray());
        cmd.Parameters.Add(new SqlParameter("@id", canonicalId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ---------- Alias (port del SELECT/INSERT de alias del resolver) ----------

    public async Task<int?> FindAliasAsync(
        SqlConnection conn, SqlTransaction tx, string advisorName, string advisorCategory, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT TOP 1 canonical_id
            FROM dbo.waf_canonical_alias
            WHERE LOWER(advisor_name) = LOWER(@name) AND LOWER(advisor_category) = LOWER(@category)
            """;
        cmd.Parameters.Add(new SqlParameter("@name", advisorName));
        cmd.Parameters.Add(new SqlParameter("@category", advisorCategory));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    public async Task SaveAliasAsync(
        SqlConnection conn, SqlTransaction tx, string advisorName, string advisorCategory,
        int canonicalId, string? matchSource, decimal? confidence, string? createdBy, CancellationToken ct)
    {
        // IF NOT EXISTS evita choque con el índice único de firma si ya fue aprendida.
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            IF NOT EXISTS (
                SELECT 1 FROM dbo.waf_canonical_alias
                WHERE LOWER(advisor_name) = LOWER(@name) AND LOWER(advisor_category) = LOWER(@category)
            )
            INSERT INTO dbo.waf_canonical_alias
                (advisor_name, advisor_category, canonical_id, match_source, confidence, created_by)
            VALUES (@name, @category, @canonicalId, @matchSource, @confidence, @createdBy)
            """;
        cmd.Parameters.Add(new SqlParameter("@name", advisorName));
        cmd.Parameters.Add(new SqlParameter("@category", advisorCategory));
        cmd.Parameters.Add(new SqlParameter("@canonicalId", canonicalId));
        cmd.Parameters.Add(new SqlParameter("@matchSource", (object?)matchSource ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@confidence", (object?)confidence ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@createdBy", (object?)createdBy ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- LoadClientCandidates (port de _fetch_excel_match_candidates) ----------

    public async Task<IReadOnlyList<WafCandidate>> LoadClientCandidatesAsync(
        SqlConnection conn, SqlTransaction tx, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT
                r.canonical_id, r.recommendation_id, r.matrix_code, c.pillar_number, c.advisor_name,
                c.advisor_category, c.review_scope_es, c.benefit_es,
                c.client_action_es, c.bit_action_es,
                COALESCE(resources.resources_text, '') AS resources_text,
                COALESCE(t.completion_pct, 0) AS completion_pct,
                t.remediation_start_date, t.execution_log,
                COALESCE(types.type_ids, '') AS type_ids
            FROM dbo.waf_recommendation r
            INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = r.canonical_id
            LEFT JOIN dbo.waf_recommendation_tracking t
                ON t.client_id = r.client_id AND t.canonical_id = r.canonical_id
            OUTER APPLY (
                SELECT STRING_AGG(CAST(f.resource_name AS NVARCHAR(MAX)), N' ') AS resources_text
                FROM dbo.waf_resource_finding f
                WHERE f.recommendation_id = r.recommendation_id AND f.status = 'active'
            ) resources
            OUTER APPLY (
                SELECT STRING_AGG(CAST(x.tid AS NVARCHAR(MAX)), N' ') AS type_ids
                FROM (
                    SELECT DISTINCT LOWER(JSON_VALUE(f2.additional_info, '$.recommendationTypeId')) AS tid
                    FROM dbo.waf_resource_finding f2
                    WHERE f2.recommendation_id = r.recommendation_id
                      AND JSON_VALUE(f2.additional_info, '$.recommendationTypeId') IS NOT NULL
                ) x
            ) types
            WHERE r.client_id = @clientId
              AND r.is_active = 1
              AND COALESCE(r.is_dismissed, 0) = 0
              AND COALESCE(c.is_excluded, 0) = 0
            ORDER BY c.pillar_number, r.matrix_code
            """;
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));

        var items = new List<WafCandidate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            items.Add(new WafCandidate(
                CanonicalId: r.GetInt32(0),
                RecommendationId: r.GetInt32(1),
                MatrixCode: r.IsDBNull(2) ? null : r.GetString(2),
                PillarNumber: r.GetByte(3),
                AdvisorName: r.GetString(4),
                AdvisorCategory: r.GetString(5),
                ReviewScopeEs: r.GetString(6),
                BenefitEs: r.IsDBNull(7) ? null : r.GetString(7),
                ClientActionEs: r.IsDBNull(8) ? null : r.GetString(8),
                BitActionEs: r.IsDBNull(9) ? null : r.GetString(9),
                ResourcesText: r.IsDBNull(10) ? "" : r.GetString(10),
                CompletionPct: r.IsDBNull(11) ? 0 : r.GetInt32(11),
                RemediationStartDate: r.IsDBNull(12) ? null : DateOnly.FromDateTime(r.GetDateTime(12)),
                ExecutionLog: r.IsDBNull(13) ? null : r.GetString(13),
                TypeIds: r.IsDBNull(14) || r.GetString(14).Length == 0
                    ? null
                    : r.GetString(14).Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }
        return items;
    }

    // ---------- typeId ARM (atajo/guarda de dedup; spec 2026-07-21) ----------

    public async Task<int?> FindCanonicalByTypeIdAsync(
        SqlConnection conn, SqlTransaction tx, string typeId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Solo hay match cuando el tipo vive en EXACTAMENTE una canónica (ambiguo => null).
        cmd.CommandText = """
            SELECT r.canonical_id
            FROM dbo.waf_resource_finding f
            INNER JOIN dbo.waf_recommendation r ON r.recommendation_id = f.recommendation_id
            WHERE LOWER(JSON_VALUE(f.additional_info, '$.recommendationTypeId')) = LOWER(@tid)
            GROUP BY r.canonical_id
            """;
        cmd.Parameters.Add(new SqlParameter("@tid", typeId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        int? found = null;
        while (await r.ReadAsync(ct))
        {
            if (found is not null) return null; // más de una canónica: ambiguo
            found = r.GetInt32(0);
        }
        return found;
    }

    public async Task<IReadOnlyList<string>> GetCanonicalTypeIdsAsync(
        SqlConnection conn, SqlTransaction tx, int canonicalId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT DISTINCT LOWER(JSON_VALUE(f.additional_info, '$.recommendationTypeId'))
            FROM dbo.waf_resource_finding f
            INNER JOIN dbo.waf_recommendation r ON r.recommendation_id = f.recommendation_id
            WHERE r.canonical_id = @id
              AND JSON_VALUE(f.additional_info, '$.recommendationTypeId') IS NOT NULL
            """;
        cmd.Parameters.Add(new SqlParameter("@id", canonicalId));
        var ids = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (!r.IsDBNull(0)) ids.Add(r.GetString(0));
        return ids;
    }

    // ---------- ApplyAiSuggestion (port de _apply_ai_suggestion) ----------

    private static readonly IReadOnlyDictionary<string, string> DecisionToStatus = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["include"] = "applied",
        ["exclude"] = "excluded",
        ["review"] = "requires_review",
    };

    public async Task ApplyAiSuggestionAsync(
        SqlConnection conn, SqlTransaction tx, int canonicalId, WafCurationResult suggestion,
        string? actor, CancellationToken ct)
    {
        var status = DecisionToStatus.TryGetValue(suggestion.Decision, out var s) ? s : "requires_review";
        var isExcluded = suggestion.Decision == "exclude" ? 1 : 0;
        var exclusionReason = string.IsNullOrWhiteSpace(suggestion.ExclusionReason) ? (object)DBNull.Value : suggestion.ExclusionReason;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE dbo.waf_recommendation_canonical
            SET pillar_number = @pillar, review_scope_es = @scope, benefit_es = @benefit,
                client_action_es = @clientAction, bit_action_es = @bitAction, is_excluded = @isExcluded,
                exclusion_reason = @exclusionReason, ai_review_status = @status, ai_decision = @decision,
                ai_confidence = @confidence, ai_possible_additional_cost = @cost,
                ai_cost_reason = @costReason, ai_exclusion_reason = @aiExclusionReason,
                ai_duplicate_group_key = @dupKey, ai_raw_model_text = @raw,
                ai_reviewed_by = @actor, ai_reviewed_at = SYSUTCDATETIME(),
                updated_at = SYSUTCDATETIME()
            WHERE canonical_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@pillar", suggestion.PillarNumber));
        cmd.Parameters.Add(new SqlParameter("@scope", suggestion.ReviewScopeEs));
        cmd.Parameters.Add(new SqlParameter("@benefit", suggestion.BenefitEs));
        cmd.Parameters.Add(new SqlParameter("@clientAction", suggestion.ClientActionEs));
        cmd.Parameters.Add(new SqlParameter("@bitAction", suggestion.BitActionEs));
        cmd.Parameters.Add(new SqlParameter("@isExcluded", isExcluded));
        cmd.Parameters.Add(new SqlParameter("@exclusionReason", exclusionReason));
        cmd.Parameters.Add(new SqlParameter("@status", status));
        cmd.Parameters.Add(new SqlParameter("@decision", suggestion.Decision));
        cmd.Parameters.Add(new SqlParameter("@confidence", suggestion.Confidence));
        cmd.Parameters.Add(new SqlParameter("@cost", suggestion.PossibleAdditionalCost ? 1 : 0));
        cmd.Parameters.Add(new SqlParameter("@costReason", string.IsNullOrWhiteSpace(suggestion.CostReason) ? (object)DBNull.Value : suggestion.CostReason));
        cmd.Parameters.Add(new SqlParameter("@aiExclusionReason", exclusionReason));
        cmd.Parameters.Add(new SqlParameter("@dupKey", string.IsNullOrWhiteSpace(suggestion.DuplicateGroupKey) ? (object)DBNull.Value : suggestion.DuplicateGroupKey));
        cmd.Parameters.Add(new SqlParameter("@raw", string.IsNullOrEmpty(suggestion.RawModelText) ? (object)DBNull.Value : suggestion.RawModelText));
        cmd.Parameters.Add(new SqlParameter("@actor", (object?)actor ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@id", canonicalId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- Pending IDs (port de _pending_ai_canonical_ids) ----------

    public async Task<IReadOnlyList<int>> ListPendingCanonicalIdsAsync(
        SqlConnection conn, SqlTransaction tx, int clientId,
        IReadOnlyList<int> preferredCanonicalIds, int limit, CancellationToken ct)
    {
        var ids = new List<int>();
        // dedupe los preferidos conservando orden.
        var seen = new HashSet<int>();
        var cleanPreferred = new List<int>();
        foreach (var id in preferredCanonicalIds ?? [])
            if (seen.Add(id)) cleanPreferred.Add(id);

        if (cleanPreferred.Count > 0)
        {
            var placeholders = string.Join(",", cleanPreferred.Select((_, i) => $"@pref{i}"));
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                SELECT c.canonical_id
                FROM dbo.waf_recommendation_canonical c
                INNER JOIN dbo.waf_recommendation r ON r.canonical_id = c.canonical_id
                WHERE r.client_id = @cid
                  AND r.is_active = 1
                  AND COALESCE(r.is_dismissed, 0) = 0
                  AND c.canonical_id IN ({placeholders})
                  AND COALESCE(c.ai_review_status, 'pending') = 'pending'
                ORDER BY c.canonical_id
                """;
            cmd.Parameters.Add(new SqlParameter("@cid", clientId));
            for (var i = 0; i < cleanPreferred.Count; i++)
                cmd.Parameters.Add(new SqlParameter($"@pref{i}", cleanPreferred[i]));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add(r.GetInt32(0));
        }

        var remaining = Math.Max(0, limit - ids.Count);
        if (remaining > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT TOP (@top) c.canonical_id
                FROM dbo.waf_recommendation_canonical c
                INNER JOIN dbo.waf_recommendation r ON r.canonical_id = c.canonical_id
                WHERE r.client_id = @cid
                  AND r.is_active = 1
                  AND COALESCE(r.is_dismissed, 0) = 0
                  AND COALESCE(c.ai_review_status, 'pending') = 'pending'
                ORDER BY c.canonical_id
                """;
            cmd.Parameters.Add(new SqlParameter("@top", remaining));
            cmd.Parameters.Add(new SqlParameter("@cid", clientId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt32(0);
                if (!ids.Contains(id)) ids.Add(id);
            }
        }
        return ids.Count > limit ? ids.Take(limit).ToList() : ids;
    }

    public async Task<IReadOnlyList<int>> ListGlobalPendingCanonicalIdsAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (@top) canonical_id
            FROM dbo.waf_recommendation_canonical
            WHERE COALESCE(ai_review_status, 'pending') = 'pending'
            ORDER BY canonical_id
            """;
        cmd.Parameters.Add(new SqlParameter("@top", limit));
        var ids = new List<int>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ids.Add(r.GetInt32(0));
        return ids;
    }

    // ---------- Mapeo ----------

    private static WafCanonical MapCanonical(SqlDataReader r) => new(
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
