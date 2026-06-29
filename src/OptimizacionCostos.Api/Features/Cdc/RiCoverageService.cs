using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Cdc;

/// <summary>
/// Cruce de instancias reservadas (RI) activas contra los recursos de un análisis. Confirmado =
/// match exacto por instanceId (Consumption); estimado = agregado por SKU/region/término.
/// Port de app/cdc/coverage.py. Persiste los confirmados en cost_results. NO toca el motor de costos.
/// </summary>
public interface IRiCoverageService
{
    Task<IReadOnlyDictionary<string, object?>> ComputeAsync(int analysisId, CancellationToken ct = default);
}

public sealed class RiCoverageService(
    ISqlConnectionFactory factory, IReservationService reservations, IAzureReservationsClient client,
    ILogger<RiCoverageService> logger) : IRiCoverageService
{
    private static readonly HashSet<string> InactiveStates = new(StringComparer.OrdinalIgnoreCase)
    { "cancelled", "canceled", "expired", "failed" };

    internal sealed record RsvWithConsumers(string? Name, string? Term, string? Product, string? Region, int? Quantity, IReadOnlyList<string> Consumers);
    internal sealed record ConfirmedMatch(int ResourceId, string? ReservationName, string? Term);

    private static string Norm(string? v) => (v ?? "").Trim().ToLowerInvariant();

    public async Task<IReadOnlyDictionary<string, object?>> ComputeAsync(int analysisId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        var clientId = await ScalarIntAsync(conn, "SELECT client_id FROM dbo.cost_analysis WHERE analysis_id = @id", analysisId, ct);
        if (clientId is null)
            return new Dictionary<string, object?> { ["confirmed_count"] = 0, ["estimated"] = Array.Empty<object>(), ["source"] = "no_analysis", ["errors"] = Array.Empty<object>(), ["updated_at"] = null };

        var resourceIdByArm = await ResourceIdByArmAsync(conn, analysisId, ct);
        var creds = await reservations.ActiveCredentialsAsync(clientId.Value, ct);

        var errors = new List<object>();
        IReadOnlyList<ReservationDto> allReservations = [];
        if (creds.Count > 0)
        {
            try
            {
                var (res, errs) = await reservations.FetchAllAsync(creds, 30, includeUtilization: false, ct);
                allReservations = res;
                errors.AddRange(errs);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Coverage: lectura de reservas fallo type={Type}", ex.GetType().Name);
                errors.Add(new { error = ex.GetType().Name });
            }
        }

        var active = allReservations.Where(r => !InactiveStates.Contains(Norm(r.State))).ToList();

        var withConsumers = new List<RsvWithConsumers>();
        foreach (var r in active)
        {
            var consumers = new List<string>();
            try
            {
                if (r.ReservationId is not null)
                {
                    var rows = await client.GetConsumersAsync(r.CredentialId, r.ReservationId, 30, ct);
                    var qty = Math.Max(1, r.Quantity ?? 1);
                    consumers = rows.Where(c => !string.IsNullOrEmpty(c.InstanceId) && c.UsedHours > 0)
                        .Take(qty).Select(c => c.InstanceId).ToList();
                }
            }
            catch (Exception ex)
            {
                logger.LogInformation("Coverage: consumers no disponibles type={Type}", ex.GetType().Name);
            }
            withConsumers.Add(new RsvWithConsumers(r.Name, r.Term, r.Product, r.Region, r.Quantity, consumers));
        }

        var confirmed = MatchConfirmed(withConsumers, resourceIdByArm);

        var confirmedByGroup = new Dictionary<(string, string, string), int>();
        foreach (var wc in withConsumers)
        {
            var key = (Norm(wc.Product), Norm(wc.Region), Norm(wc.Term));
            var matched = MatchConfirmed([wc], resourceIdByArm).Count;
            confirmedByGroup[key] = confirmedByGroup.GetValueOrDefault(key) + matched;
        }

        var estimated = AggregateReservations(withConsumers, confirmedByGroup);

        var updatedAt = DateTimeOffset.UtcNow;
        await ExecAsync(conn, "UPDATE dbo.cost_results SET ri_coverage = NULL, ri_reservation_name = NULL, ri_term = NULL WHERE analysis_id = @id", analysisId, ct);
        foreach (var c in confirmed)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE dbo.cost_results
                SET ri_coverage = 'confirmed', ri_reservation_name = @name, ri_term = @term, ri_coverage_updated_at = @ts
                WHERE analysis_id = @aid AND resource_id = @rid
                """;
            cmd.Parameters.Add(new SqlParameter("@name", (object?)c.ReservationName ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@term", (object?)c.Term ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@ts", updatedAt.UtcDateTime));
            cmd.Parameters.Add(new SqlParameter("@aid", analysisId));
            cmd.Parameters.Add(new SqlParameter("@rid", c.ResourceId));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var hasEstimated = estimated.Any(g => (int)(g["estimated_units"] ?? 0) > 0);
        var source = confirmed.Count > 0 && hasEstimated ? "partial"
            : confirmed.Count > 0 ? "consumption"
            : active.Count > 0 ? "estimated_only" : "no_reservations";

        return new Dictionary<string, object?>
        {
            ["confirmed_count"] = confirmed.Count,
            ["estimated"] = estimated,
            ["source"] = source,
            ["errors"] = errors,
            ["updated_at"] = updatedAt.ToString("o"),
        };
    }

    // -------------------- lógica pura (testeable) --------------------

    internal static List<ConfirmedMatch> MatchConfirmed(IReadOnlyList<RsvWithConsumers> reservations, IReadOnlyDictionary<string, int> resourceIdByArm)
    {
        var seen = new HashSet<int>();
        var outl = new List<ConfirmedMatch>();
        foreach (var r in reservations)
            foreach (var iid in r.Consumers)
                if (resourceIdByArm.TryGetValue(Norm(iid), out var rid) && seen.Add(rid))
                    outl.Add(new ConfirmedMatch(rid, r.Name, r.Term));
        return outl;
    }

    internal static List<Dictionary<string, object?>> AggregateReservations(
        IReadOnlyList<RsvWithConsumers> reservations, IReadOnlyDictionary<(string, string, string), int> confirmedByGroup)
    {
        var grouped = new Dictionary<(string, string, string), Dictionary<string, object?>>();
        foreach (var r in reservations)
        {
            var key = (Norm(r.Product), Norm(r.Region), Norm(r.Term));
            if (!grouped.TryGetValue(key, out var bucket))
                bucket = grouped[key] = new Dictionary<string, object?>
                { ["product"] = r.Product, ["region"] = r.Region, ["term"] = r.Term, ["reserved"] = 0 };
            bucket["reserved"] = (int)bucket["reserved"]! + (r.Quantity ?? 0);
        }
        var outl = new List<Dictionary<string, object?>>();
        foreach (var (key, bucket) in grouped)
        {
            var confirmed = confirmedByGroup.GetValueOrDefault(key);
            bucket["confirmed"] = confirmed;
            bucket["estimated_units"] = Math.Max(0, (int)bucket["reserved"]! - confirmed);
            outl.Add(bucket);
        }
        return outl;
    }

    // -------------------- helpers DB --------------------

    private static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF COL_LENGTH('dbo.cost_results', 'ri_coverage') IS NULL ALTER TABLE dbo.cost_results ADD ri_coverage NVARCHAR(20) NULL;
            IF COL_LENGTH('dbo.cost_results', 'ri_reservation_name') IS NULL ALTER TABLE dbo.cost_results ADD ri_reservation_name NVARCHAR(300) NULL;
            IF COL_LENGTH('dbo.cost_results', 'ri_term') IS NULL ALTER TABLE dbo.cost_results ADD ri_term NVARCHAR(10) NULL;
            IF COL_LENGTH('dbo.cost_results', 'ri_coverage_updated_at') IS NULL ALTER TABLE dbo.cost_results ADD ri_coverage_updated_at DATETIME2 NULL;
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

    private static async Task<Dictionary<string, int>> ResourceIdByArmAsync(SqlConnection conn, int analysisId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT resource_id, azure_resource_id FROM dbo.azure_resources WHERE analysis_id = @id AND azure_resource_id IS NOT NULL";
        cmd.Parameters.Add(new SqlParameter("@id", analysisId));
        var map = new Dictionary<string, int>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[Norm(r.GetString(1))] = r.GetInt32(0);
        return map;
    }

    private static async Task ExecAsync(SqlConnection conn, string sql, int id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
