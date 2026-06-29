using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Implementación SQL del Advisor Score store. Port de las funciones de BD de
/// app/waf/advisor_score.py. Microsoft.Data.SqlClient async, SQL parametrizado, schema lazy.
/// </summary>
public sealed class SqlAdvisorScoreStore(ISqlConnectionFactory factory) : IAdvisorScoreStore
{
    private static readonly int[] Pillars = [1, 2, 3, 4, 5];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // ensure_ascii=False
    };

    // ---------------------------------------------------------------------
    // list_active_client_ids
    // ---------------------------------------------------------------------
    public async Task<IReadOnlyList<int>> ListActiveClientIdsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT client_id
            FROM dbo.clients
            WHERE COALESCE(is_active, 1) = 1
            ORDER BY client_name
            """;
        var ids = new List<int>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            ids.Add(r.GetInt32(0));
        return ids;
    }

    // ---------------------------------------------------------------------
    // list_client_subscription_groups
    // ---------------------------------------------------------------------
    public async Task<IReadOnlyDictionary<int, WafSubscriptionGroup>> LoadClientSubscriptionGroupsAsync(
        int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.credential_id, c.credential_name, s.subscription_id, s.subscription_name
            FROM dbo.client_azure_subscriptions s
            INNER JOIN dbo.client_azure_credentials c ON c.credential_id = s.credential_id
            WHERE s.client_id = @cid
              AND s.is_active = 1
              AND COALESCE(s.is_managed, 1) = 1
              AND c.is_active = 1
            ORDER BY c.credential_name, s.subscription_name
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));

        // Preserva orden de inserción (dict de Python) -> credenciales en el orden del ORDER BY.
        var order = new List<int>();
        var names = new Dictionary<int, string>();
        var subs = new Dictionary<int, Dictionary<string, string>>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var credentialId = reader.GetInt32(0);
            var credentialName = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var subscriptionId = reader.GetString(2);
            var subscriptionName = reader.IsDBNull(3) ? subscriptionId : reader.GetString(3);
            if (string.IsNullOrEmpty(subscriptionName)) subscriptionName = subscriptionId;

            if (!subs.TryGetValue(credentialId, out var subMap))
            {
                subMap = subs[credentialId] = new Dictionary<string, string>(StringComparer.Ordinal);
                names[credentialId] = credentialName;
                order.Add(credentialId);
            }
            subMap[subscriptionId] = subscriptionName;
        }

        var groups = new Dictionary<int, WafSubscriptionGroup>();
        foreach (var credentialId in order)
            groups[credentialId] = new WafSubscriptionGroup(credentialId, names[credentialId], subs[credentialId]);
        return groups;
    }

    // ---------------------------------------------------------------------
    // persist_advisor_score_snapshot (MERGE idempotente)
    // ---------------------------------------------------------------------
    public async Task<WafAdvisorScoreSnapshot?> PersistSnapshotAsync(
        int clientId, WafAdvisorScoreResult score, DateOnly? snapshotDate,
        string source, bool includeInReports, CancellationToken ct = default)
    {
        var date = snapshotDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var src = (string.IsNullOrWhiteSpace(source) ? "scheduled" : source).Trim().ToLowerInvariant();
        if (src.Length > 20) src = src[..20];

        var status = string.IsNullOrEmpty(score.Status) ? "unknown" : score.Status;
        var hasConnection = score.HasConnection ? 1 : 0;
        var scored = score.SubscriptionsScored;
        decimal?[] values = Pillars
            .Select(p => score.Pillars.TryGetValue(p.ToString(), out var v) ? (decimal?)v : null)
            .ToArray();
        var warningsJson = JsonSerializer.Serialize(score.Warnings, JsonOpts);
        var breakdownJson = JsonSerializer.Serialize(BreakdownToJsonShape(score.Breakdown), JsonOpts);
        var includeBit = includeInReports ? 1 : 0;

        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            MERGE dbo.waf_advisor_score_snapshot AS target
            USING (SELECT @client_id AS client_id, @snapshot_date AS snapshot_date, @source AS source) AS src
            ON target.client_id = src.client_id
               AND target.snapshot_date = src.snapshot_date
               AND target.source = src.source
            WHEN MATCHED THEN
                UPDATE SET captured_at = SYSUTCDATETIME(),
                           status = @status,
                           has_connection = @has_connection,
                           subscriptions_scored = @scored,
                           score_p1 = @p1, score_p2 = @p2, score_p3 = @p3, score_p4 = @p4, score_p5 = @p5,
                           warnings_json = @warnings,
                           breakdown_json = @breakdown,
                           include_in_reports = @include
            WHEN NOT MATCHED THEN
                INSERT (
                    client_id, snapshot_date, status, has_connection, subscriptions_scored,
                    score_p1, score_p2, score_p3, score_p4, score_p5,
                    warnings_json, breakdown_json, source, include_in_reports
                )
                VALUES (
                    @client_id, @snapshot_date, @status, @has_connection, @scored,
                    @p1, @p2, @p3, @p4, @p5,
                    @warnings, @breakdown, @source, @include
                );
            """;
        cmd.Parameters.Add(new SqlParameter("@client_id", clientId));
        cmd.Parameters.Add(new SqlParameter("@snapshot_date", date.ToDateTime(TimeOnly.MinValue)) { SqlDbType = System.Data.SqlDbType.Date });
        cmd.Parameters.Add(new SqlParameter("@source", src));
        cmd.Parameters.Add(new SqlParameter("@status", status));
        cmd.Parameters.Add(new SqlParameter("@has_connection", hasConnection));
        cmd.Parameters.Add(new SqlParameter("@scored", scored));
        cmd.Parameters.Add(new SqlParameter("@p1", (object?)values[0] ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@p2", (object?)values[1] ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@p3", (object?)values[2] ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@p4", (object?)values[3] ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@p5", (object?)values[4] ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@warnings", warningsJson));
        cmd.Parameters.Add(new SqlParameter("@breakdown", breakdownJson));
        cmd.Parameters.Add(new SqlParameter("@include", includeBit));
        await cmd.ExecuteNonQueryAsync(ct);

        return await LoadLatestSnapshotAsync(conn, clientId, includeBreakdown: true, ct);
    }

    // ---------------------------------------------------------------------
    // load_latest_advisor_score_snapshot
    // ---------------------------------------------------------------------
    public async Task<WafAdvisorScoreSnapshot?> LoadLatestSnapshotAsync(
        int clientId, bool includeBreakdown = false, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        return await LoadLatestSnapshotAsync(conn, clientId, includeBreakdown, ct);
    }

    private static async Task<WafAdvisorScoreSnapshot?> LoadLatestSnapshotAsync(
        SqlConnection conn, int clientId, bool includeBreakdown, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TOP 1 snapshot_id, client_id, snapshot_date, captured_at, status, has_connection,
                   subscriptions_scored, score_p1, score_p2, score_p3, score_p4, score_p5,
                   warnings_json, breakdown_json, source, include_in_reports
            FROM dbo.waf_advisor_score_snapshot
            WHERE client_id = @cid
            ORDER BY captured_at DESC, snapshot_date DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        decimal? Dec(int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
        return new WafAdvisorScoreSnapshot(
            SnapshotId: r.GetInt32(0),
            ClientId: r.GetInt32(1),
            SnapshotDate: DateOnly.FromDateTime(r.GetDateTime(2)),
            CapturedAt: r.GetDateTime(3),
            Status: r.IsDBNull(4) ? "unknown" : r.GetString(4),
            HasConnection: !r.IsDBNull(5) && r.GetBoolean(5),
            SubscriptionsScored: r.IsDBNull(6) ? 0 : r.GetInt32(6),
            ScoreP1: Dec(7), ScoreP2: Dec(8), ScoreP3: Dec(9), ScoreP4: Dec(10), ScoreP5: Dec(11),
            WarningsJson: r.IsDBNull(12) ? null : r.GetString(12),
            // include_breakdown=False -> no se expone el breakdown (paridad con snapshot_row_to_response)
            BreakdownJson: includeBreakdown && !r.IsDBNull(13) ? r.GetString(13) : null,
            Source: r.IsDBNull(14) ? "scheduled" : r.GetString(14),
            IncludeInReports: r.IsDBNull(15) || r.GetBoolean(15));
    }

    // ---------------------------------------------------------------------
    // try_acquire_job_lock
    // ---------------------------------------------------------------------
    public async Task<bool> TryAcquireJobLockAsync(
        string jobName, int leaseSeconds = 21600, CancellationToken ct = default)
    {
        var owner = $"{Environment.MachineName}-{Guid.NewGuid()}";
        try
        {
            await using var conn = await factory.OpenAsync(ct);
            await WafSchema.EnsureWafSchemaAsync(conn, ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SET NOCOUNT ON;
                DECLARE @now DATETIME2 = SYSUTCDATETIME();
                UPDATE dbo.app_job_lock
                SET locked_until = DATEADD(second, @lease, @now), owner = @owner, updated_at = @now
                WHERE job_name = @job AND locked_until <= @now;

                IF @@ROWCOUNT = 0 AND NOT EXISTS (
                    SELECT 1 FROM dbo.app_job_lock WHERE job_name = @job
                )
                BEGIN
                    INSERT INTO dbo.app_job_lock (job_name, locked_until, owner, updated_at)
                    VALUES (@job, DATEADD(second, @lease, @now), @owner, @now);
                END;

                SELECT CASE
                    WHEN owner = @owner AND locked_until > @now THEN 1 ELSE 0
                END AS acquired
                FROM dbo.app_job_lock
                WHERE job_name = @job;
                """;
            cmd.Parameters.Add(new SqlParameter("@lease", leaseSeconds));
            cmd.Parameters.Add(new SqlParameter("@owner", owner));
            cmd.Parameters.Add(new SqlParameter("@job", jobName));

            var result = await cmd.ExecuteScalarAsync(ct);
            return result is not (null or DBNull) && Convert.ToInt32(result) == 1;
        }
        catch
        {
            // Paridad con Python: no se pudo adquirir -> false (no se propaga).
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // build_advisor_score_history: historial 12 meses para el informe mensual.
    // ---------------------------------------------------------------------
    private static readonly string[] MonthNames =
    [
        "", "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio",
        "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre",
    ];

    private static (int Year, int Month) AddMonths(int year, int month, int delta)
    {
        var zeroBased = (year * 12 + (month - 1)) + delta;
        // Python usa // y % (floor division). Equivalente aquí porque zeroBased >= 0 en el rango de
        // fechas válido del informe (12 meses hacia atrás desde un periodo real).
        return (zeroBased / 12, (zeroBased % 12) + 1);
    }

    private static string MonthKey(int year, int month) => $"{year:D4}-{month:D2}";

    private static double? SnapshotAverage(JsonObject? snapshot)
    {
        if (snapshot?["pillars"] is not JsonObject scores) return null;
        var values = new List<double>();
        foreach (var kv in scores)
            if (kv.Value is JsonValue v && v.TryGetValue<double>(out var d)) values.Add(d);
        if (values.Count == 0) return null;
        return Math.Round(values.Sum() / values.Count, 2, MidpointRounding.ToEven);
    }

    public async Task<JsonObject> BuildScoreHistoryAsync(int clientId, int year, int month, CancellationToken ct = default)
    {
        var (startYear, startMonth) = AddMonths(year, month, -11);
        var startDate = new DateTime(startYear, startMonth, 1);
        var endDate = new DateTime(year, month, DateTime.DaysInMonth(year, month));

        JsonObject Empty() => new()
        {
            ["current"] = null, ["previous"] = null, ["delta"] = new JsonObject(), ["series"] = new JsonArray(),
        };

        var byMonth = new Dictionary<string, JsonObject>();
        var series = new JsonArray();
        try
        {
            await using var conn = await factory.OpenAsync(ct);
            await WafSchema.EnsureWafSchemaAsync(conn, ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT *
                FROM (
                    SELECT client_id, snapshot_date, captured_at, status, has_connection,
                           subscriptions_scored, score_p1, score_p2, score_p3, score_p4, score_p5,
                           warnings_json, breakdown_json,
                           source, include_in_reports,
                           ROW_NUMBER() OVER (
                               PARTITION BY YEAR(snapshot_date), MONTH(snapshot_date)
                               ORDER BY snapshot_date DESC, captured_at DESC
                           ) AS rn
                    FROM dbo.waf_advisor_score_snapshot
                    WHERE client_id = @cid
                      AND snapshot_date BETWEEN @start AND @end
                      AND COALESCE(include_in_reports, 1) = 1
                ) ranked
                WHERE rn = 1
                ORDER BY snapshot_date
                """;
            cmd.Parameters.Add(new SqlParameter("@cid", clientId));
            cmd.Parameters.Add(new SqlParameter("@start", startDate) { SqlDbType = System.Data.SqlDbType.Date });
            cmd.Parameters.Add(new SqlParameter("@end", endDate) { SqlDbType = System.Data.SqlDbType.Date });

            await using var r = await cmd.ExecuteReaderAsync(ct);
            var idx = BuildOrdinalMap(r);
            while (await r.ReadAsync(ct))
            {
                var snapshot = SnapshotRowToResponse(r, idx);
                var snapDateText = snapshot["snapshot_date"]?.GetValue<string>();
                if (snapDateText is null || !DateOnly.TryParse(snapDateText, CultureInfo.InvariantCulture, out var snapDate))
                    continue;
                var key = MonthKey(snapDate.Year, snapDate.Month);
                var item = new JsonObject
                {
                    ["month"] = key,
                    ["label"] = $"{MonthNames[snapDate.Month]} {snapDate.Year}",
                    ["snapshot_date"] = snapshot["snapshot_date"]?.DeepClone(),
                    ["captured_at"] = snapshot["captured_at"]?.DeepClone(),
                    ["status"] = snapshot["status"]?.DeepClone(),
                    ["scores"] = (snapshot["pillars"] as JsonObject)?.DeepClone() ?? new JsonObject(),
                    ["average"] = SnapshotAverage(snapshot) is { } avg ? JsonValue.Create(avg) : null,
                };
                byMonth[key] = snapshot;
                series.Add(item);
            }
        }
        catch
        {
            // Python: logger.info(...); return empty. El historial es best-effort.
            return Empty();
        }

        var currentKey = MonthKey(year, month);
        var (prevYear, prevMonth) = AddMonths(year, month, -1);
        var previousKey = MonthKey(prevYear, prevMonth);
        var current = byMonth.GetValueOrDefault(currentKey);
        var previous = byMonth.GetValueOrDefault(previousKey);

        var currentScores = current?["pillars"] as JsonObject;
        var previousScores = previous?["pillars"] as JsonObject;
        var delta = new JsonObject();
        foreach (var pillar in Pillars)
        {
            var key = pillar.ToString();
            if (currentScores?[key] is JsonValue cv && cv.TryGetValue<double>(out var c) &&
                previousScores?[key] is JsonValue pv && pv.TryGetValue<double>(out var p))
                delta[key] = JsonValue.Create(Math.Round(c - p, 2, MidpointRounding.ToEven));
            else
                delta[key] = null;
        }

        return new JsonObject
        {
            ["current"] = current?.DeepClone(),
            ["previous"] = previous?.DeepClone(),
            ["delta"] = delta,
            ["series"] = series,
        };
    }

    private static Dictionary<string, int> BuildOrdinalMap(SqlDataReader r)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < r.FieldCount; i++) map[r.GetName(i)] = i;
        return map;
    }

    // Port de snapshot_row_to_response (include_breakdown=False) -> JsonObject con las claves del Python.
    private static JsonObject SnapshotRowToResponse(SqlDataReader r, IReadOnlyDictionary<string, int> idx)
    {
        object? Val(string name) => idx.TryGetValue(name, out var i) && !r.IsDBNull(i) ? r.GetValue(i) : null;

        var scores = new JsonObject();
        foreach (var pillar in Pillars)
        {
            if (Val($"score_p{pillar}") is { } v)
                scores[pillar.ToString()] = JsonValue.Create(Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture), 2, MidpointRounding.ToEven));
        }

        var snapshotDate = Val("snapshot_date");
        var capturedAt = Val("captured_at");
        var includeRaw = Val("include_in_reports");

        return new JsonObject
        {
            ["client_id"] = Convert.ToInt32(Val("client_id") ?? 0),
            ["has_connection"] = Val("has_connection") is { } hc && Convert.ToBoolean(hc),
            ["subscriptions_scored"] = Convert.ToInt32(Val("subscriptions_scored") ?? 0),
            ["pillars"] = scores,
            ["warnings"] = ParseJsonArray(Val("warnings_json") as string),
            ["status"] = Val("status") as string ?? "unknown",
            ["source"] = Val("source") as string ?? "scheduled",
            ["include_in_reports"] = includeRaw is null || Convert.ToBoolean(includeRaw),
            ["snapshot_date"] = DateText(snapshotDate),
            ["captured_at"] = DateTimeText(capturedAt),
            ["stale"] = IsStale(snapshotDate),
        };
    }

    private static JsonArray ParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonArray();
        try
        {
            return JsonNode.Parse(json) as JsonArray ?? new JsonArray();
        }
        catch
        {
            return new JsonArray();
        }
    }

    private static string? DateText(object? value) => value switch
    {
        null => null,
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => (value.ToString() ?? "") is { Length: >= 10 } s ? s[..10] : value.ToString(),
    };

    private static string? DateTimeText(object? value) => value switch
    {
        null => null,
        DateTime dt => DateTime.SpecifyKind(dt, dt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dt.Kind)
            .ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static bool IsStale(object? snapshotDate)
    {
        var text = DateText(snapshotDate);
        if (string.IsNullOrEmpty(text) || !DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
            return true;
        return parsed < DateOnly.FromDateTime(DateTime.Today).AddDays(-8);
    }

    // ---------------------------------------------------------------------
    // Serialización del breakdown al shape JSON del Python:
    // [{subscription_id, subscription_name, scores: {"1": {score, weight}, ...}}]
    // ---------------------------------------------------------------------
    private static IReadOnlyList<Dictionary<string, object?>> BreakdownToJsonShape(
        IReadOnlyList<WafScoreBreakdown> breakdown)
    {
        var list = new List<Dictionary<string, object?>>(breakdown.Count);
        foreach (var b in breakdown)
        {
            var scores = new Dictionary<string, object?>();
            foreach (var (pillar, data) in b.Scores)
                scores[pillar] = new Dictionary<string, object?> { ["score"] = data.Score, ["weight"] = data.Weight };
            list.Add(new Dictionary<string, object?>
            {
                ["subscription_id"] = b.SubscriptionId,
                ["subscription_name"] = b.SubscriptionName,
                ["scores"] = scores,
            });
        }
        return list;
    }
}
