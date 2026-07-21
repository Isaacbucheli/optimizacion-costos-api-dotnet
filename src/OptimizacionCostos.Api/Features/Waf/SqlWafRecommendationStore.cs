using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Implementación SQL de la matriz por cliente: listado/detalle/findings, descarte, agregados
/// (summary/sections) y utilidades de ingesta (recalcular counts, renumerar códigos). Schema lazy:
/// los métodos públicos de entrada abren conexión propia y aseguran el schema; los métodos con
/// (conn, tx) participan en la transacción de ingesta del caller. SQL siempre parametrizado.
/// </summary>
public sealed class SqlWafRecommendationStore(ISqlConnectionFactory factory) : IWafRecommendationStore
{
    private const string RecommendationSelect = """
        SELECT recommendation_id, client_id, canonical_id, matrix_code,
               business_impact, impact_number, resource_count,
               first_seen_at, last_seen_at, is_active, is_dismissed,
               dismissed_at, dismissed_by, dismissal_reason
        FROM dbo.waf_recommendation
        """;

    /// <summary>Etiquetas de sección por pilar (port de PILLAR_SECTION_NAMES de waf.py).</summary>
    private static readonly IReadOnlyDictionary<int, string> PillarSectionNames = new Dictionary<int, string>
    {
        [1] = "Eficiencia del rendimiento",
        [2] = "Excelencia operacional",
        [3] = "Seguridad",
        [4] = "Confiabilidad",
        [5] = "Optimizacion de costos",
    };

    // ---------- Listado / detalle ----------

    public async Task<IReadOnlyList<WafRecommendation>> ListAsync(
        int clientId, byte? pillar, bool activeOnly, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        var where = new List<string> { "r.client_id = @clientId" };
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));
        if (activeOnly)
        {
            where.Add("r.is_active = 1");
            where.Add("COALESCE(r.is_dismissed, 0) = 0");
        }
        if (pillar is byte p)
        {
            where.Add("c.pillar_number = @pillar");
            cmd.Parameters.Add(new SqlParameter("@pillar", p));
        }

        cmd.CommandText = $"""
            SELECT r.recommendation_id, r.client_id, r.canonical_id, r.matrix_code,
                   r.business_impact, r.impact_number, r.resource_count,
                   r.first_seen_at, r.last_seen_at, r.is_active, r.is_dismissed,
                   r.dismissed_at, r.dismissed_by, r.dismissal_reason
            FROM dbo.waf_recommendation r
            INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = r.canonical_id
            WHERE {string.Join(" AND ", where)}
            ORDER BY c.pillar_number, r.impact_number, c.review_scope_es
            """;

        var items = new List<WafRecommendation>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            items.Add(MapRecommendation(rd));
        return items;
    }

    public async Task<WafRecommendation?> GetAsync(int clientId, int canonicalId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"{RecommendationSelect} WHERE client_id = @clientId AND canonical_id = @canonicalId " +
            "AND COALESCE(is_dismissed, 0) = 0";
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));
        cmd.Parameters.Add(new SqlParameter("@canonicalId", canonicalId));

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? MapRecommendation(rd) : null;
    }

    public async Task MarkReadAsync(int clientId, int canonicalId, string userKey, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM dbo.waf_recommendation_read
                           WHERE client_id = @cid AND canonical_id = @canon AND user_key = @user)
                INSERT INTO dbo.waf_recommendation_read (client_id, canonical_id, user_key)
                VALUES (@cid, @canon, @user);
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@canon", canonicalId));
        cmd.Parameters.Add(new SqlParameter("@user", userKey ?? ""));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<WafResourceFinding>> ListResourcesAsync(
        int clientId, int canonicalId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.finding_id, f.recommendation_id, f.resource_name, f.resource_type, f.resource_group,
                   f.subscription_id, f.subscription_name, f.azure_resource_id,
                   f.business_impact, f.additional_info, f.first_seen_at, f.last_seen_at,
                   f.status, f.resolved_at
            FROM dbo.waf_resource_finding f
            INNER JOIN dbo.waf_recommendation r ON r.recommendation_id = f.recommendation_id
            WHERE r.client_id = @clientId AND r.canonical_id = @canonicalId
              AND COALESCE(r.is_dismissed, 0) = 0
            ORDER BY f.status, f.resource_name
            """;
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));
        cmd.Parameters.Add(new SqlParameter("@canonicalId", canonicalId));

        var items = new List<WafResourceFinding>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            items.Add(MapFinding(rd));
        return items;
    }

    // ---------- Dismiss (port de dismiss_waf_recommendation) ----------

    public async Task DismissAsync(
        int clientId, int canonicalId, string? reason, string? actor, CancellationToken ct = default)
    {
        var dismissalReason = Trunc(string.IsNullOrWhiteSpace(reason)
            ? "Descartada por decision del cliente" : reason!, 1000);

        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // Localiza la recomendación y su estado.
            int recommendationId;
            bool alreadyDismissed;
            await using (var find = conn.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = """
                    SELECT recommendation_id, is_dismissed
                    FROM dbo.waf_recommendation
                    WHERE client_id = @clientId AND canonical_id = @canonicalId
                    """;
                find.Parameters.Add(new SqlParameter("@clientId", clientId));
                find.Parameters.Add(new SqlParameter("@canonicalId", canonicalId));
                await using var r = await find.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    throw new KeyNotFoundException("Recommendation not found");
                recommendationId = r.GetInt32(0);
                alreadyDismissed = r.GetBoolean(1);
            }
            if (alreadyDismissed)
            {
                await tx.CommitAsync(ct);
                return; // idempotente
            }

            // Findings activos → 'dismissed'.
            await using (var f = conn.CreateCommand())
            {
                f.Transaction = tx;
                f.CommandText = """
                    UPDATE dbo.waf_resource_finding
                    SET status = 'dismissed', resolved_at = SYSUTCDATETIME()
                    WHERE recommendation_id = @recId AND status = 'active'
                    """;
                f.Parameters.Add(new SqlParameter("@recId", recommendationId));
                await f.ExecuteNonQueryAsync(ct);
            }

            // Recomendación → descartada.
            await using (var u = conn.CreateCommand())
            {
                u.Transaction = tx;
                u.CommandText = """
                    UPDATE dbo.waf_recommendation
                    SET is_active = 0, is_dismissed = 1, dismissed_at = SYSUTCDATETIME(),
                        dismissed_by = @actor, dismissal_reason = @reason
                    WHERE recommendation_id = @recId
                    """;
                u.Parameters.Add(new SqlParameter("@actor", (object?)actor ?? DBNull.Value));
                u.Parameters.Add(new SqlParameter("@reason", dismissalReason));
                u.Parameters.Add(new SqlParameter("@recId", recommendationId));
                await u.ExecuteNonQueryAsync(ct);
            }

            // Historial.
            await using (var h = conn.CreateCommand())
            {
                h.Transaction = tx;
                h.CommandText = """
                    INSERT INTO dbo.waf_tracking_history (
                        client_id, canonical_id, field_changed, old_value, new_value, changed_by
                    )
                    VALUES (@clientId, @canonicalId, 'recommendation_dismissed', 'active', @reason, @actor)
                    """;
                h.Parameters.Add(new SqlParameter("@clientId", clientId));
                h.Parameters.Add(new SqlParameter("@canonicalId", canonicalId));
                h.Parameters.Add(new SqlParameter("@reason", dismissalReason));
                h.Parameters.Add(new SqlParameter("@actor", (object?)actor ?? DBNull.Value));
                await h.ExecuteNonQueryAsync(ct);
            }

            await RenumberMatrixCodesAsync(conn, tx, clientId, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ---------- Summary / Sections ----------

    public async Task<WafClientSummary> GetSummaryAsync(int clientId, bool excludeSecurityPillar = false, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        // Filtro constante (literal, no viene de input) para omitir el pilar de seguridad si el
        // cliente lo gestiona externamente (Gestión de Vulnerabilidades).
        var secFilter = excludeSecurityPillar ? $" AND c.pillar_number <> {WafConstants.SecurityPillar}" : "";

        int recommendations = 0, activeRecs = 0, costRecs = 0, activeFindings = 0;
        await using (var counts = conn.CreateCommand())
        {
            counts.CommandText = $"""
                SELECT
                    COUNT(DISTINCT r.recommendation_id) AS recommendations,
                    COUNT(DISTINCT CASE WHEN r.is_active = 1 AND COALESCE(r.is_dismissed, 0) = 0 THEN r.recommendation_id END) AS active_recommendations,
                    COUNT(DISTINCT CASE WHEN c.pillar_number = 5 AND r.is_active = 1 AND COALESCE(r.is_dismissed, 0) = 0 THEN r.recommendation_id END) AS cost_recommendations,
                    COUNT(DISTINCT CASE WHEN f.status = 'active' AND COALESCE(r.is_dismissed, 0) = 0 THEN f.finding_id END) AS active_findings
                FROM dbo.waf_recommendation r
                INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = r.canonical_id
                LEFT JOIN dbo.waf_resource_finding f ON f.recommendation_id = r.recommendation_id
                WHERE r.client_id = @clientId{secFilter}
                """;
            counts.Parameters.Add(new SqlParameter("@clientId", clientId));
            await using var r = await counts.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                recommendations = r.IsDBNull(0) ? 0 : r.GetInt32(0);
                activeRecs = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                costRecs = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                activeFindings = r.IsDBNull(3) ? 0 : r.GetInt32(3);
            }
        }

        WafLatestIngestion? latest = null;
        await using (var lat = conn.CreateCommand())
        {
            lat.CommandText = """
                SELECT TOP 1 run_id, source_file_name, status, rows_total, rows_processed,
                       started_at, completed_at
                FROM dbo.waf_ingestion_run
                WHERE client_id = @clientId
                ORDER BY started_at DESC
                """;
            lat.Parameters.Add(new SqlParameter("@clientId", clientId));
            await using var r = await lat.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                latest = new WafLatestIngestion(
                    RunId: r.GetInt32(0),
                    SourceFileName: r.GetString(1),
                    Status: r.GetString(2),
                    RowsTotal: r.IsDBNull(3) ? null : r.GetInt32(3),
                    RowsProcessed: r.IsDBNull(4) ? null : r.GetInt32(4),
                    StartedAt: r.GetDateTime(5).ToString("o"),
                    CompletedAt: r.IsDBNull(6) ? null : r.GetDateTime(6).ToString("o"));
            }
        }

        return new WafClientSummary(clientId, recommendations, activeRecs, costRecs, activeFindings, latest);
    }

    public async Task<IReadOnlyList<WafSection>> GetSectionsAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        // Datos agregados por pilar; los pilares sin filas quedan en cero al ensamblar.
        var byPillar = new Dictionary<int, (int Total, int Resources, double Avg, int High, int Medium)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    c.pillar_number,
                    COUNT(*) AS total_recs,
                    COALESCE(SUM(r.resource_count), 0) AS total_resources,
                    COALESCE(AVG(CAST(COALESCE(t.completion_pct, 0) AS FLOAT)), 0) AS avg_progress,
                    SUM(CASE WHEN r.impact_number = 1 THEN 1 ELSE 0 END) AS high_recs,
                    SUM(CASE WHEN r.impact_number = 2 THEN 1 ELSE 0 END) AS medium_recs
                FROM dbo.waf_recommendation r
                INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = r.canonical_id
                LEFT JOIN dbo.waf_recommendation_tracking t
                    ON t.client_id = r.client_id AND t.canonical_id = r.canonical_id
                WHERE r.client_id = @clientId
                  AND r.is_active = 1
                  AND COALESCE(r.is_dismissed, 0) = 0
                GROUP BY c.pillar_number
                """;
            cmd.Parameters.Add(new SqlParameter("@clientId", clientId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                int pillar = r.GetByte(0);
                byPillar[pillar] = (
                    Total: r.IsDBNull(1) ? 0 : r.GetInt32(1),
                    Resources: r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2)),
                    Avg: Math.Round(r.IsDBNull(3) ? 0.0 : r.GetDouble(3), 1),
                    High: r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4)),
                    Medium: r.IsDBNull(5) ? 0 : Convert.ToInt32(r.GetValue(5)));
            }
        }

        var sections = new List<WafSection>(5);
        for (var pillar = 1; pillar <= 5; pillar++)
        {
            var data = byPillar.TryGetValue(pillar, out var v) ? v : (0, 0, 0.0, 0, 0);
            sections.Add(new WafSection(
                SectionNum: pillar,
                SectionName: PillarSectionNames[pillar],
                TotalRecs: data.Item1,
                TotalResources: data.Item2,
                AvgProgress: data.Item3,
                HighRecs: data.Item4,
                MediumRecs: data.Item5));
        }
        return sections;
    }

    // ---------- Utilidades de ingesta (participan en la transacción del caller) ----------

    public async Task RecalculateResourceCountsAsync(
        SqlConnection conn, SqlTransaction tx, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE r
            SET resource_count = counts.resource_count,
                is_active = CASE
                    WHEN COALESCE(r.is_dismissed, 0) = 1 THEN 0
                    WHEN counts.resource_count > 0 THEN 1
                    ELSE 0
                END
            FROM dbo.waf_recommendation r
            CROSS APPLY (
                SELECT COUNT(*) AS resource_count
                FROM dbo.waf_resource_finding f
                WHERE f.recommendation_id = r.recommendation_id AND f.status = 'active'
            ) counts
            WHERE r.client_id = @clientId
            """;
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RenumberMatrixCodesAsync(
        SqlConnection conn, SqlTransaction tx, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            WITH ranked AS (
                SELECT r.recommendation_id,
                       CONCAT(c.pillar_number, '.', CAST(ROW_NUMBER() OVER (
                           PARTITION BY c.pillar_number
                           ORDER BY r.impact_number, c.review_scope_es, r.canonical_id
                       ) AS varchar(10))) AS code
                FROM dbo.waf_recommendation r
                INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = r.canonical_id
                WHERE r.client_id = @clientId
                  AND r.is_active = 1
                  AND COALESCE(r.is_dismissed, 0) = 0
            )
            UPDATE r
            SET r.matrix_code = ranked.code
            FROM dbo.waf_recommendation r
            INNER JOIN ranked ON ranked.recommendation_id = r.recommendation_id
            """;
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- Mapeo / helpers ----------

    private static WafRecommendation MapRecommendation(SqlDataReader r) => new(
        RecommendationId: r.GetInt32(0),
        ClientId: r.GetInt32(1),
        CanonicalId: r.GetInt32(2),
        MatrixCode: r.GetString(3),
        BusinessImpact: r.GetString(4),
        ImpactNumber: r.GetByte(5),
        ResourceCount: r.GetInt32(6),
        FirstSeenAt: r.GetDateTime(7),
        LastSeenAt: r.GetDateTime(8),
        IsActive: r.GetBoolean(9),
        IsDismissed: r.GetBoolean(10),
        DismissedAt: r.IsDBNull(11) ? null : r.GetDateTime(11),
        DismissedBy: r.IsDBNull(12) ? null : r.GetString(12),
        DismissalReason: r.IsDBNull(13) ? null : r.GetString(13));

    private static WafResourceFinding MapFinding(SqlDataReader r) => new(
        FindingId: r.GetInt64(0),
        RecommendationId: r.GetInt32(1),
        ResourceName: r.GetString(2),
        ResourceType: r.GetString(3),
        ResourceGroup: r.GetString(4),
        SubscriptionId: r.GetString(5),
        SubscriptionName: r.GetString(6),
        AzureResourceId: r.IsDBNull(7) ? null : r.GetString(7),
        BusinessImpact: r.GetString(8),
        AdditionalInfo: r.IsDBNull(9) ? null : r.GetString(9),
        FirstSeenAt: r.GetDateTime(10),
        LastSeenAt: r.GetDateTime(11),
        Status: r.GetString(12),
        ResolvedAt: r.IsDBNull(13) ? null : r.GetDateTime(13));

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];
}
