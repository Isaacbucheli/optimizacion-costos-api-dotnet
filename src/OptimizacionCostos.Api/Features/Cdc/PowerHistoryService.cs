using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.AzureIntegration;

namespace OptimizacionCostos.Api.Features.Cdc;

/// <summary>
/// Historial de encendido/apagado de VMs desde el Azure Activity Log (mes calendario anterior).
/// Port de app/power_history.py. Calcula horas encendida / uptime por VM y persiste en
/// dbo.vm_power_usage. Solo lectura del Activity Log (Reader). Degrada sin romper.
/// </summary>
public interface IPowerHistoryService
{
    Task<IReadOnlyDictionary<string, object?>> ComputeAsync(int analysisId, CancellationToken ct = default);

    /// <summary>
    /// Variante para el informe mensual (port de compute_uptime_map): con una credencial ya
    /// construida + sus suscripciones, lee el Activity Log del periodo y devuelve por cada VM en
    /// <paramref name="runningByArm"/> (arm_lower → corriendo ahora) sus horas encendida y % uptime.
    /// Best-effort: si el Activity Log falla en TODAS las suscripciones, devuelve un mapa vacío.
    /// </summary>
    Task<IReadOnlyDictionary<string, (double RunningHours, double UptimePct)>> ComputeUptimeMapAsync(
        Azure.Core.TokenCredential credential, IReadOnlyList<string> subscriptions,
        IReadOnlyDictionary<string, bool> runningByArm, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);
}

public sealed class PowerHistoryService(
    ISqlConnectionFactory factory, IAzureCredentialFactory credentials, IHttpClientFactory httpFactory,
    ILogger<PowerHistoryService> logger) : IPowerHistoryService
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string Arm = "https://management.azure.com";
    private const string ActivityLogApi = "2015-04-01";

    private static readonly HashSet<string> StartOps = new(StringComparer.OrdinalIgnoreCase)
    { "microsoft.compute/virtualmachines/start/action" };
    private static readonly HashSet<string> StopOps = new(StringComparer.OrdinalIgnoreCase)
    { "microsoft.compute/virtualmachines/deallocate/action", "microsoft.compute/virtualmachines/poweroff/action" };

    internal sealed record PowerEvent(string ResourceId, string Kind, DateTimeOffset When, string? By);

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();
    private static bool IsRunning(string? powerState) => Norm(powerState).Contains("running");

    public async Task<IReadOnlyDictionary<string, object?>> ComputeAsync(int analysisId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        var clientId = await ScalarIntAsync(conn, "SELECT client_id FROM dbo.cost_analysis WHERE analysis_id = @id", analysisId, ct);
        if (clientId is null)
            return Summary(0, 0, null, null, "no_analysis", [], null);

        // VMs del análisis (arm_lower -> {resource_id, running}).
        var vmByArm = new Dictionary<string, (int ResourceId, bool Running)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT r.resource_id, r.azure_resource_id, d.power_state
                FROM dbo.azure_resources r LEFT JOIN dbo.vm_details d ON d.resource_id = r.resource_id
                WHERE r.analysis_id = @id AND r.resource_type = 'microsoft.compute/virtualmachines' AND r.azure_resource_id IS NOT NULL
                """;
            cmd.Parameters.Add(new SqlParameter("@id", analysisId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                vmByArm[Norm(r.GetString(1))] = (r.GetInt32(0), IsRunning(r.IsDBNull(2) ? null : r.GetString(2)));
        }

        // Suscripciones activas+administradas agrupadas por credencial.
        var subsByCred = new Dictionary<int, List<string>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT s.credential_id, s.subscription_id
                FROM dbo.client_azure_subscriptions s
                INNER JOIN dbo.client_azure_credentials c ON s.credential_id = c.credential_id
                WHERE s.client_id = @cid AND s.is_active = 1 AND COALESCE(s.is_managed, 1) = 1 AND c.is_active = 1
                """;
            cmd.Parameters.Add(new SqlParameter("@cid", clientId.Value));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!subsByCred.TryGetValue(r.GetInt32(0), out var l)) l = subsByCred[r.GetInt32(0)] = [];
                l.Add(r.GetString(1));
            }
        }

        var (start, end) = PreviousCalendarMonth(DateTimeOffset.UtcNow);
        var eventsByVm = new Dictionary<string, List<PowerEvent>>();
        var errors = new List<object>();

        if (vmByArm.Count > 0 && subsByCred.Count > 0)
        {
            foreach (var (credentialId, subIds) in subsByCred)
            {
                string token;
                try
                {
                    var cred = await credentials.GetClientSecretCredentialAsync(credentialId, ct);
                    token = (await cred.GetTokenAsync(new TokenRequestContext([ArmScope]), ct)).Token;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "PowerHistory: credencial {Cred} no disponible", credentialId);
                    errors.Add(new { credential_id = credentialId });
                    continue;
                }
                var http = httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(30);
                foreach (var sub in subIds)
                {
                    try
                    {
                        foreach (var ev in await FetchSubscriptionEventsAsync(http, token, sub, start, end, ct))
                            if (vmByArm.ContainsKey(ev.ResourceId))
                            {
                                if (!eventsByVm.TryGetValue(ev.ResourceId, out var l)) l = eventsByVm[ev.ResourceId] = [];
                                l.Add(ev);
                            }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "PowerHistory: Activity Log sub falló type={Type}", ex.GetType().Name);
                        errors.Add(new { credential_id = credentialId });
                    }
                }
            }
        }

        var updatedAt = DateTimeOffset.UtcNow;
        await ExecAsync(conn, "DELETE FROM dbo.vm_power_usage WHERE analysis_id = @id", analysisId, ct);
        var updated = 0;
        foreach (var (arm, vm) in vmByArm)
        {
            var events = eventsByVm.TryGetValue(arm, out var l) ? l : [];
            var usage = ComputeUptime(events, vm.Running, start, end);
            var eventsJson = JsonSerializer.Serialize(events.OrderBy(e => e.When)
                .Select(e => new Dictionary<string, object?> { ["kind"] = e.Kind, ["when"] = e.When.ToString("o"), ["by"] = e.By }));
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dbo.vm_power_usage
                    (analysis_id, resource_id, period_start, period_end, running_hours, uptime_pct, event_count, events_json, updated_at)
                VALUES (@aid, @rid, @ps, @pe, @rh, @up, @ec, @ej, @ts)
                """;
            cmd.Parameters.Add(new SqlParameter("@aid", analysisId));
            cmd.Parameters.Add(new SqlParameter("@rid", vm.ResourceId));
            cmd.Parameters.Add(new SqlParameter("@ps", start.UtcDateTime));
            cmd.Parameters.Add(new SqlParameter("@pe", end.UtcDateTime));
            cmd.Parameters.Add(new SqlParameter("@rh", Math.Round(usage.RunningHours, 2)));
            cmd.Parameters.Add(new SqlParameter("@up", Math.Round(usage.UptimePct, 1)));
            cmd.Parameters.Add(new SqlParameter("@ec", events.Count));
            cmd.Parameters.Add(new SqlParameter("@ej", eventsJson));
            cmd.Parameters.Add(new SqlParameter("@ts", updatedAt.UtcDateTime));
            await cmd.ExecuteNonQueryAsync(ct);
            updated++;
        }

        var source = eventsByVm.Count > 0 ? "activity_log" : (vmByArm.Count > 0 ? "no_events" : "no_vms");
        return Summary(updated, eventsByVm.Count, start.ToString("o"), end.ToString("o"), source, errors, updatedAt.ToString("o"));
    }

    public async Task<IReadOnlyDictionary<string, (double RunningHours, double UptimePct)>> ComputeUptimeMapAsync(
        Azure.Core.TokenCredential credential, IReadOnlyList<string> subscriptions,
        IReadOnlyDictionary<string, bool> runningByArm, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        var token = (await credential.GetTokenAsync(new Azure.Core.TokenRequestContext([ArmScope]), ct)).Token;
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        var eventsByVm = new Dictionary<string, List<PowerEvent>>();
        var ok = 0;
        foreach (var sub in subscriptions)
        {
            try
            {
                foreach (var ev in await FetchSubscriptionEventsAsync(http, token, sub, start, end, ct))
                    if (runningByArm.ContainsKey(ev.ResourceId))
                    {
                        if (!eventsByVm.TryGetValue(ev.ResourceId, out var l)) l = eventsByVm[ev.ResourceId] = [];
                        l.Add(ev);
                    }
                ok++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PowerHistory(map): Activity Log sub falló type={Type}", ex.GetType().Name);
            }
        }
        if (ok == 0) return new Dictionary<string, (double, double)>();

        var outMap = new Dictionary<string, (double RunningHours, double UptimePct)>();
        foreach (var (arm, running) in runningByArm)
        {
            var events = eventsByVm.TryGetValue(arm, out var l) ? l : [];
            var usage = ComputeUptime(events, running, start, end);
            outMap[arm] = (Math.Round(usage.RunningHours, 1), Math.Round(usage.UptimePct, 1));
        }
        return outMap;
    }

    // -------------------- lógica pura (testeable) --------------------

    /// <summary>[inicio, fin) del mes calendario anterior, en UTC. fin = primer día del mes actual.</summary>
    internal static (DateTimeOffset Start, DateTimeOffset End) PreviousCalendarMonth(DateTimeOffset now)
    {
        var end = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var start = end.AddDays(-1);
        start = new DateTimeOffset(start.Year, start.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return (start, end);
    }

    /// <summary>Reconstruye segmentos encendidos de UNA VM en [start, end). Port de compute_uptime.</summary>
    internal static (double RunningHours, double PeriodHours, double UptimePct) ComputeUptime(
        IReadOnlyList<PowerEvent> events, bool currentRunning, DateTimeOffset start, DateTimeOffset end)
    {
        var periodSeconds = (end - start).TotalSeconds;
        var periodHours = periodSeconds / 3600.0;
        var inWindow = events.Where(e => e.When >= start && e.When <= end).OrderBy(e => e.When).ToList();
        if (inWindow.Count == 0)
            return (Math.Round(currentRunning ? periodHours : 0.0, 4), Math.Round(periodHours, 4), currentRunning ? 100.0 : 0.0);

        var stateRunning = inWindow[0].Kind == "stop";
        var cursor = start;
        var runningSeconds = 0.0;
        foreach (var e in inWindow)
        {
            var when = e.When < start ? start : (e.When > end ? end : e.When);
            if (stateRunning) runningSeconds += (when - cursor).TotalSeconds;
            cursor = when;
            stateRunning = e.Kind == "start";
        }
        if (stateRunning) runningSeconds += (end - cursor).TotalSeconds;

        var uptime = periodSeconds > 0 ? runningSeconds / periodSeconds * 100.0 : 0.0;
        return (Math.Round(runningSeconds / 3600.0, 4), Math.Round(periodHours, 4), Math.Round(uptime, 4));
    }

    /// <summary>De un evento crudo del Activity Log extrae el evento de energía normalizado, o null.</summary>
    internal static PowerEvent? NormalizeEvent(JsonElement raw)
    {
        string? Sub(string parent, string child) =>
            raw.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(child, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var status = Norm(Sub("status", "value"));
        if (status != "succeeded") return null;
        var operation = Norm(Sub("operationName", "value"));
        string kind;
        if (StartOps.Contains(operation)) kind = "start";
        else if (StopOps.Contains(operation)) kind = "stop";
        else return null;
        var resourceId = Norm(raw.TryGetProperty("resourceId", out var ri) && ri.ValueKind == JsonValueKind.String ? ri.GetString() : null);
        var when = ParseTs(raw.TryGetProperty("eventTimestamp", out var ts) && ts.ValueKind == JsonValueKind.String ? ts.GetString() : null);
        if (string.IsNullOrEmpty(resourceId) || when is null) return null;
        var by = raw.TryGetProperty("caller", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        return new PowerEvent(resourceId, kind, when.Value, by);
    }

    internal static DateTimeOffset? ParseTs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;
    }

    private async Task<List<PowerEvent>> FetchSubscriptionEventsAsync(HttpClient http, string token, string sub, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var filter = $"eventTimestamp ge '{start:yyyy-MM-ddTHH:mm:ssZ}' and eventTimestamp le '{end:yyyy-MM-ddTHH:mm:ssZ}' and resourceProvider eq 'Microsoft.Compute'";
        string? url = $"{Arm}/subscriptions/{sub}/providers/Microsoft.Insights/eventtypes/management/values"
                    + $"?api-version={ActivityLogApi}&$filter={Uri.EscapeDataString(filter)}&$select=operationName,status,eventTimestamp,resourceId,caller";
        var events = new List<PowerEvent>();
        while (url is not null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (root?["value"] is JsonArray arr)
                foreach (var item in arr)
                    if (item is not null && NormalizeEvent(JsonSerializer.Deserialize<JsonElement>(item.ToJsonString())) is { } ev)
                        events.Add(ev);
            url = root?["nextLink"]?.GetValue<string>();
        }
        return events;
    }

    // -------------------- helpers DB --------------------

    private static IReadOnlyDictionary<string, object?> Summary(int updated, int withEvents, string? ps, string? pe, string source, IReadOnlyList<object> errors, string? updatedAt) =>
        new Dictionary<string, object?>
        {
            ["updated_count"] = updated, ["vms_with_events"] = withEvents, ["period_start"] = ps,
            ["period_end"] = pe, ["source"] = source, ["errors"] = errors, ["updated_at"] = updatedAt,
        };

    private static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.vm_power_usage', 'U') IS NULL
            CREATE TABLE dbo.vm_power_usage (
                power_usage_id INT IDENTITY(1,1) PRIMARY KEY, analysis_id INT NOT NULL, resource_id INT NOT NULL,
                period_start DATETIME2 NULL, period_end DATETIME2 NULL, running_hours DECIMAL(10,2) NULL,
                uptime_pct DECIMAL(5,1) NULL, event_count INT NULL, events_json NVARCHAR(MAX) NULL, updated_at DATETIME2 NULL);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_vm_power_usage_analysis' AND object_id = OBJECT_ID('dbo.vm_power_usage'))
            CREATE INDEX IX_vm_power_usage_analysis ON dbo.vm_power_usage (analysis_id, resource_id);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int?> ScalarIntAsync(SqlConnection conn, string sql, int id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@id", id));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToInt32(v);
    }

    private static async Task ExecAsync(SqlConnection conn, string sql, int id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
