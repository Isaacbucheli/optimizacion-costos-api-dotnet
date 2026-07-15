using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Implementación de la ingesta WAF. Port 1:1 de app/waf/ingestion.py. Maneja la transacción de la
/// corrida; delega canónicas/alias en IWafCatalogStore y recalcular/renumerar en IWafRecommendationStore.
/// </summary>
public sealed partial class SqlWafIngestionStore(
    ISqlConnectionFactory factory, IWafCatalogStore catalog, IWafRecommendationStore recommendations) : IWafIngestionStore
{
    private static readonly IReadOnlySet<string> RequiredColumns = new HashSet<string>(StringComparer.Ordinal)
        { "Recommendation", "Category", "Business Impact", "Resource Name" };
    private static readonly string[] ExcludedNamePatterns = ["microsoft defender", "defender cspm", "azure firewall"];
    private static readonly IReadOnlySet<string> CostImplicationExclude = new HashSet<string>(StringComparer.Ordinal) { "low", "medium", "high" };
    private static readonly IReadOnlyDictionary<string, byte> ImpactToNumber = new Dictionary<string, byte>(StringComparer.Ordinal)
        { ["high"] = 1, ["medium"] = 2, ["low"] = 3 };
    private static readonly IReadOnlySet<string> IgnoredCols = new HashSet<string>(StringComparer.Ordinal)
    {
        "Recommendation", "Category", "Business Impact", "Resource Name",
        "Type", "Resource Group", "Subscription ID", "Subscription Name",
    };

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    private static string Norm(string? v) => string.IsNullOrEmpty(v) ? "" : Whitespace().Replace(v.Trim(), " ");

    // -------------------- PARSE CSV --------------------

    public (IReadOnlyList<AdvisorRow> Rows, WafIngestionMetrics Metrics) ParseAdvisorCsv(byte[] content)
    {
        var text = DecodeCsv(content);
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var (header, dataStart) = FindHeader(lines);
        if (header is null)
            throw new InvalidOperationException("El CSV no contiene las columnas esperadas de Azure Advisor.");

        var rows = new List<AdvisorRow>();
        int skipped = 0, duplicates = 0;
        var seen = new HashSet<(string, string, string, string)>();

        for (var i = dataStart; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = ParseCsvLine(lines[i]);
            var raw = MapRow(header, cells);

            var advisorName = Norm(raw.GetValueOrDefault("Recommendation"));
            var advisorCategory = Norm(raw.GetValueOrDefault("Category"));
            if (advisorName.Length == 0 || advisorCategory.Length == 0) { skipped++; continue; }
            if (ExcludedNamePatterns.Any(p => advisorName.ToLowerInvariant().Contains(p))) { skipped++; continue; }
            if (CostImplicationExclude.Contains(Norm(raw.GetValueOrDefault("Cost implications (Preview)")).ToLowerInvariant())) { skipped++; continue; }

            var row = new AdvisorRow(
                Trunc(advisorName, 500), Trunc(advisorCategory, 100),
                Trunc(Norm(raw.GetValueOrDefault("Business Impact")) is { Length: > 0 } bi ? bi : "Medium", 30),
                Trunc(Norm(raw.GetValueOrDefault("Resource Name")), 512),
                Trunc(Norm(raw.GetValueOrDefault("Type")), 300),
                Trunc(Norm(raw.GetValueOrDefault("Resource Group")), 255),
                Trunc(Norm(raw.GetValueOrDefault("Subscription ID")), 100),
                Trunc(Norm(raw.GetValueOrDefault("Subscription Name")), 255),
                Trunc(Norm(raw.GetValueOrDefault("Resource ID") ?? raw.GetValueOrDefault("Resource Id")), 2048),
                AdditionalInfo(raw));

            var key = (row.AdvisorName.ToLowerInvariant(), row.AdvisorCategory.ToLowerInvariant(),
                       row.ResourceName.ToLowerInvariant(), row.SubscriptionId.ToLowerInvariant());
            if (!seen.Add(key)) { duplicates++; continue; }
            rows.Add(row);
        }

        var metrics = new WafIngestionMetrics(rows.Count + skipped + duplicates, rows.Count, skipped, duplicates, 0, "", "");
        return (rows, metrics);
    }

    private static string Trunc(string s, int max) => s.Length > max ? s[..max] : s;

    private static string DecodeCsv(byte[] content)
    {
        // utf-8-sig / utf-8 estricto → cp1252 → latin-1 (paridad con _decode_csv).
        try
        {
            var s = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
            return s.Length > 0 && s[0] == '﻿' ? s[1..] : s;
        }
        catch (DecoderFallbackException) { }
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(content);
        }
        catch (Exception) { return Encoding.Latin1.GetString(content); }
    }

    private static (Dictionary<int, string>? Header, int DataStart) FindHeader(string[] lines)
    {
        foreach (var offset in new[] { 0, 1 })
        {
            if (offset >= lines.Length) break;
            var cells = ParseCsvLine(lines[offset]);
            var names = cells.Select(Norm).ToList();
            if (RequiredColumns.IsSubsetOf(names))
            {
                var map = new Dictionary<int, string>();
                for (var i = 0; i < names.Count; i++) map[i] = names[i];
                return (map, offset + 1);
            }
        }
        return (null, 0);
    }

    private static Dictionary<string, string> MapRow(Dictionary<int, string> header, string[] cells)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (idx, name) in header)
            d[name] = idx < cells.Length ? cells[idx] : "";
        return d;
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return [.. result];
    }

    private static string? AdditionalInfo(Dictionary<string, string> raw)
    {
        var details = new Dictionary<string, string>();
        foreach (var (k, v) in raw)
        {
            if (IgnoredCols.Contains(k)) continue;
            var nv = Norm(v);
            if (nv.Length > 0) details[k] = nv;
        }
        return details.Count > 0 ? JsonSerializer.Serialize(details) : null;
    }

    private static string MatrixCode(int pillar, string advisorName, string advisorCategory)
    {
        var digest = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{advisorName}|{advisorCategory}")))[..8].ToUpperInvariant();
        return $"AUTO-{pillar}-{digest}";
    }

    // -------------------- INGEST --------------------

    public async Task<WafIngestionResult> IngestAdvisorRowsAsync(
        int clientId, string sourceName, IReadOnlyList<AdvisorRow> rows, string? createdBy,
        WafIngestionMetrics? metrics, IReadOnlyList<string>? replaceSubscriptionIds,
        Func<SqlConnection, SqlTransaction, AdvisorRow, Task<int?>>? dedupResolver,
        IReadOnlyList<object>? warnings = null, IReadOnlyList<object>? subscriptionResults = null,
        CancellationToken ct = default)
    {
        metrics ??= new WafIngestionMetrics(rows.Count, rows.Count, 0, 0, 0, "", "");
        warnings ??= [];
        var now = DateTime.UtcNow;

        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        var runId = await CreateRunAsync(conn, tx, clientId, sourceName, createdBy, ct);
        await Exec(conn, tx, ct, "UPDATE dbo.waf_ingestion_run SET rows_total = @t, rows_processed = @p WHERE run_id = @id",
            ("@t", metrics.RowsTotal), ("@p", metrics.RowsProcessed), ("@id", runId));

        // Resolver findings ausentes (los manuales 'importado' NUNCA se resuelven).
        if (replaceSubscriptionIds is null)
        {
            await Exec(conn, tx, ct, """
                UPDATE f SET f.status = 'resolved', f.resolved_at = @now
                FROM dbo.waf_resource_finding f INNER JOIN dbo.waf_recommendation r ON r.recommendation_id = f.recommendation_id
                WHERE r.client_id = @cid AND f.status = 'active' AND f.subscription_id <> @manual
                """, ("@now", now), ("@cid", clientId), ("@manual", WafConstants.ManualSubscriptionId));
        }
        else if (replaceSubscriptionIds.Count > 0)
        {
            var inParams = replaceSubscriptionIds.Select((_, i) => $"@s{i}").ToList();
            await using var cmd = NewCmd(conn, tx, $"""
                UPDATE f SET f.status = 'resolved', f.resolved_at = @now
                FROM dbo.waf_resource_finding f INNER JOIN dbo.waf_recommendation r ON r.recommendation_id = f.recommendation_id
                WHERE r.client_id = @cid AND f.status = 'active' AND f.subscription_id <> @manual AND f.subscription_id IN ({string.Join(",", inParams)})
                """);
            cmd.Parameters.AddRange([new SqlParameter("@now", now), new SqlParameter("@cid", clientId), new SqlParameter("@manual", WafConstants.ManualSubscriptionId)]);
            for (var i = 0; i < replaceSubscriptionIds.Count; i++) cmd.Parameters.Add(new SqlParameter($"@s{i}", replaceSubscriptionIds[i]));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var newRecommendations = 0;
        var newFindings = 0;
        var newCanonicalIds = new List<int>();
        foreach (var row in rows)
        {
            var (canonicalId, pillar, created) = await catalog.GetOrCreateCanonicalAsync(conn, tx, row, dedupResolver, ct);
            if (created) newCanonicalIds.Add(canonicalId);
            var (recId, recCreated, dismissed) = await UpsertRecommendationAsync(conn, tx, clientId, canonicalId, pillar, row, now, ct);
            if (dismissed) continue;
            if (recCreated) newRecommendations++;
            if (await UpsertFindingAsync(conn, tx, recId, row, now, ct)) newFindings++;
        }

        await recommendations.RecalculateResourceCountsAsync(conn, tx, clientId, ct);
        await recommendations.RenumberMatrixCodesAsync(conn, tx, clientId, ct);

        var resolvedFindings = await ScalarInt(conn, tx, ct, """
            SELECT COUNT(*) FROM dbo.waf_resource_finding f INNER JOIN dbo.waf_recommendation r ON r.recommendation_id = f.recommendation_id
            WHERE r.client_id = @cid AND f.status = 'resolved' AND f.resolved_at = @now
            """, ("@cid", clientId), ("@now", now));

        await Exec(conn, tx, ct, """
            UPDATE dbo.waf_ingestion_run
            SET status = @status, completed_at = @now, new_recommendations = @nr, new_findings = @nf,
                resolved_findings = @rf, unmapped_recommendations = @warn, subscription_results = @subres, error_message = @err
            WHERE run_id = @id
            """,
            ("@status", warnings.Count > 0 ? "completed_with_errors" : "completed"), ("@now", now),
            ("@nr", newRecommendations), ("@nf", newFindings), ("@rf", resolvedFindings),
            ("@warn", warnings.Count > 0 ? Trunc(JsonSerializer.Serialize(warnings), 4000) : (object)DBNull.Value),
            ("@subres", subscriptionResults is { Count: > 0 } ? Trunc(JsonSerializer.Serialize(subscriptionResults), 8000) : (object)DBNull.Value),
            ("@err", warnings.Count > 0 ? $"{warnings.Count} suscripcion(es) con error parcial" : (object)DBNull.Value),
            ("@id", runId));

        await tx.CommitAsync(ct);
        return new WafIngestionResult(runId, metrics, newRecommendations, newCanonicalIds, newFindings, resolvedFindings, warnings);
    }

    private async Task<int> CreateRunAsync(SqlConnection conn, SqlTransaction tx, int clientId, string sourceName, string? createdBy, CancellationToken ct)
    {
        await using var cmd = NewCmd(conn, tx,
            "INSERT INTO dbo.waf_ingestion_run (client_id, source_file_name, status, started_at, created_by) OUTPUT INSERTED.run_id VALUES (@cid, @src, 'running', @now, @by)");
        cmd.Parameters.AddRange([
            new SqlParameter("@cid", clientId), new SqlParameter("@src", Trunc(sourceName, 400)),
            new SqlParameter("@now", DateTime.UtcNow), new SqlParameter("@by", (object?)createdBy ?? DBNull.Value)]);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private async Task<(int RecId, bool Created, bool Dismissed)> UpsertRecommendationAsync(
        SqlConnection conn, SqlTransaction tx, int clientId, int canonicalId, int pillar, AdvisorRow row, DateTime now, CancellationToken ct)
    {
        await using (var find = NewCmd(conn, tx, "SELECT recommendation_id, is_dismissed FROM dbo.waf_recommendation WHERE client_id = @cid AND canonical_id = @canon"))
        {
            find.Parameters.AddRange([new SqlParameter("@cid", clientId), new SqlParameter("@canon", canonicalId)]);
            await using var r = await find.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                var recId = r.GetInt32(0);
                var dismissed = !r.IsDBNull(1) && r.GetBoolean(1);
                await r.DisposeAsync();
                if (dismissed) return (recId, false, true);
                var impactNum = ImpactToNumber.GetValueOrDefault(row.BusinessImpact.ToLowerInvariant(), (byte)2);
                await Exec(conn, tx, ct, """
                    UPDATE dbo.waf_recommendation SET matrix_code = @code, business_impact = @impact, impact_number = @num, last_seen_at = @now, is_active = 1
                    WHERE recommendation_id = @id
                    """, ("@code", MatrixCode(pillar, row.AdvisorName, row.AdvisorCategory)), ("@impact", row.BusinessImpact),
                    ("@num", impactNum), ("@now", now), ("@id", recId));
                return (recId, false, false);
            }
        }
        var impactNumber = ImpactToNumber.GetValueOrDefault(row.BusinessImpact.ToLowerInvariant(), (byte)2);
        await using var ins = NewCmd(conn, tx, """
            INSERT INTO dbo.waf_recommendation (client_id, canonical_id, matrix_code, business_impact, impact_number, first_seen_at, last_seen_at)
            OUTPUT INSERTED.recommendation_id VALUES (@cid, @canon, @code, @impact, @num, @now, @now)
            """);
        ins.Parameters.AddRange([
            new SqlParameter("@cid", clientId), new SqlParameter("@canon", canonicalId),
            new SqlParameter("@code", MatrixCode(pillar, row.AdvisorName, row.AdvisorCategory)),
            new SqlParameter("@impact", row.BusinessImpact), new SqlParameter("@num", impactNumber), new SqlParameter("@now", now)]);
        return (Convert.ToInt32(await ins.ExecuteScalarAsync(ct)), true, false);
    }

    private async Task<bool> UpsertFindingAsync(SqlConnection conn, SqlTransaction tx, int recId, AdvisorRow row, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(row.ResourceName)) return false;
        await using (var find = NewCmd(conn, tx, "SELECT TOP 1 finding_id FROM dbo.waf_resource_finding WHERE recommendation_id = @rid AND resource_name = @name AND subscription_id = @sub"))
        {
            find.Parameters.AddRange([new SqlParameter("@rid", recId), new SqlParameter("@name", row.ResourceName), new SqlParameter("@sub", row.SubscriptionId)]);
            var existing = await find.ExecuteScalarAsync(ct);
            if (existing is not null and not DBNull)
            {
                await Exec(conn, tx, ct, """
                    UPDATE dbo.waf_resource_finding SET resource_type = @type, resource_group = @rg, subscription_name = @sn,
                        azure_resource_id = @arid, business_impact = @impact, additional_info = @info, last_seen_at = @now, status = 'active', resolved_at = NULL
                    WHERE finding_id = @fid
                    """, ("@type", row.ResourceType), ("@rg", row.ResourceGroup), ("@sn", row.SubscriptionName),
                    ("@arid", (object?)NullIfEmpty(row.AzureResourceId) ?? DBNull.Value), ("@impact", row.BusinessImpact),
                    ("@info", (object?)row.AdditionalInfo ?? DBNull.Value), ("@now", now), ("@fid", Convert.ToInt64(existing)));
                return false;
            }
        }
        await Exec(conn, tx, ct, """
            INSERT INTO dbo.waf_resource_finding (recommendation_id, resource_name, resource_type, resource_group,
                subscription_id, subscription_name, azure_resource_id, business_impact, additional_info, first_seen_at, last_seen_at)
            VALUES (@rid, @name, @type, @rg, @sub, @sn, @arid, @impact, @info, @now, @now)
            """, ("@rid", recId), ("@name", row.ResourceName), ("@type", row.ResourceType), ("@rg", row.ResourceGroup),
            ("@sub", row.SubscriptionId), ("@sn", row.SubscriptionName), ("@arid", (object?)NullIfEmpty(row.AzureResourceId) ?? DBNull.Value),
            ("@impact", row.BusinessImpact), ("@info", (object?)row.AdditionalInfo ?? DBNull.Value), ("@now", now));
        return true;
    }

    public async Task<int> MarkStaleRunsFailedAsync(int maxAgeHours = 24, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.waf_ingestion_run SET status = 'failed', completed_at = @now, error_message = @msg
            WHERE status = 'running' AND started_at < DATEADD(hour, @age, @now)
            """;
        cmd.Parameters.Add(new SqlParameter("@now", DateTime.UtcNow));
        cmd.Parameters.Add(new SqlParameter("@msg", "Run marcado como fallido por exceder la ventana maxima de seguimiento. Puede reintentar Consultar Advisor."));
        cmd.Parameters.Add(new SqlParameter("@age", -Math.Abs(maxAgeHours)));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------- helpers --------------------
    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static SqlCommand NewCmd(SqlConnection conn, SqlTransaction tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static async Task Exec(SqlConnection conn, SqlTransaction tx, CancellationToken ct, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = NewCmd(conn, tx, sql);
        foreach (var (n, v) in ps) cmd.Parameters.Add(new SqlParameter(n, v));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> ScalarInt(SqlConnection conn, SqlTransaction tx, CancellationToken ct, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = NewCmd(conn, tx, sql);
        foreach (var (n, v) in ps) cmd.Parameters.Add(new SqlParameter(n, v));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }
}
