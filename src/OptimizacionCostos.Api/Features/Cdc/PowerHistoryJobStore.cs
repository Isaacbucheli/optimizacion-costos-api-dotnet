using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Cdc;

/// <summary>Estado del job de power-history para polling desde el front.</summary>
public sealed record PowerHistoryJobStatus(
    string Status, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? SummaryJson, string? Error);

/// <summary>
/// Estado del job de refresh de encendido/apagado (tabla dbo.power_history_job, una fila por análisis).
/// MarkRunning limpia finished/summary/error; MarkCompleted fija el summary; MarkFailed fija el error.
/// </summary>
public interface IPowerHistoryJobStore
{
    Task MarkRunningAsync(int analysisId, string? actor, CancellationToken ct);
    Task MarkCompletedAsync(int analysisId, string summaryJson, CancellationToken ct);
    Task MarkFailedAsync(int analysisId, string error, CancellationToken ct);
    Task<PowerHistoryJobStatus?> GetStatusAsync(int analysisId, CancellationToken ct);
    Task<bool> IsRunningAsync(int analysisId, CancellationToken ct);

    /// <summary>Marca como 'failed' toda fila 'running' huérfana (p.ej. tras reinicio del worker,
    /// cuando la cola en memoria se perdió). Devuelve cuántas filas se marcaron. Permite que el guard
    /// IsRunning no bloquee reintentos permanentemente.</summary>
    Task<int> MarkOrphanedRunningAsFailedAsync(string error, CancellationToken ct);
}

public sealed class SqlPowerHistoryJobStore(ISqlConnectionFactory factory) : IPowerHistoryJobStore
{
    public async Task MarkRunningAsync(int analysisId, string? actor, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        var now = DateTimeOffset.UtcNow.UtcDateTime;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            MERGE dbo.power_history_job AS t
            USING (SELECT @id AS analysis_id) AS s ON t.analysis_id = s.analysis_id
            WHEN MATCHED THEN UPDATE SET status = 'running', started_at = @ts, finished_at = NULL,
                summary_json = NULL, error = NULL, updated_at = @ts
            WHEN NOT MATCHED THEN INSERT (analysis_id, status, started_at, finished_at, summary_json, error, updated_at)
                VALUES (@id, 'running', @ts, NULL, NULL, NULL, @ts);
            """;
        cmd.Parameters.Add(new SqlParameter("@id", analysisId));
        cmd.Parameters.Add(new SqlParameter("@ts", now));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkCompletedAsync(int analysisId, string summaryJson, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        var now = DateTimeOffset.UtcNow.UtcDateTime;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.power_history_job
            SET status = 'completed', finished_at = @ts, summary_json = @sj, error = NULL, updated_at = @ts
            WHERE analysis_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", analysisId));
        cmd.Parameters.Add(new SqlParameter("@ts", now));
        cmd.Parameters.Add(new SqlParameter("@sj", (object?)summaryJson ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(int analysisId, string error, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        var now = DateTimeOffset.UtcNow.UtcDateTime;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.power_history_job
            SET status = 'failed', finished_at = @ts, error = @err, summary_json = NULL, updated_at = @ts
            WHERE analysis_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", analysisId));
        cmd.Parameters.Add(new SqlParameter("@ts", now));
        cmd.Parameters.Add(new SqlParameter("@err", (object?)error ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<PowerHistoryJobStatus?> GetStatusAsync(int analysisId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT status, started_at, finished_at, summary_json, error
            FROM dbo.power_history_job WHERE analysis_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", analysisId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new PowerHistoryJobStatus(
            r.GetString(0),
            r.IsDBNull(1) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc)),
            r.IsDBNull(2) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc)),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4));
    }

    public async Task<bool> IsRunningAsync(int analysisId, CancellationToken ct)
    {
        var status = await GetStatusAsync(analysisId, ct);
        return status?.Status == "running";
    }

    public async Task<int> MarkOrphanedRunningAsFailedAsync(string error, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        var now = DateTimeOffset.UtcNow.UtcDateTime;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.power_history_job
            SET status = 'failed', finished_at = @ts, error = @err, summary_json = NULL, updated_at = @ts
            WHERE status = 'running'
            """;
        cmd.Parameters.Add(new SqlParameter("@ts", now));
        cmd.Parameters.Add(new SqlParameter("@err", (object?)error ?? DBNull.Value));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.power_history_job', 'U') IS NULL
            CREATE TABLE dbo.power_history_job (
                analysis_id INT NOT NULL PRIMARY KEY,
                status VARCHAR(20) NOT NULL,
                started_at DATETIME2 NULL,
                finished_at DATETIME2 NULL,
                summary_json NVARCHAR(MAX) NULL,
                error NVARCHAR(MAX) NULL,
                updated_at DATETIME2 NULL);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
