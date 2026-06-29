using System.Globalization;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>Fila del índice de informes (para el listado). summary_json se devuelve crudo.</summary>
public sealed record ReportIndexItem(
    int ReportId, int Year, int Month, string Status, bool IsPartial,
    string? SummaryJson, string? GeneratedBy, string GeneratedAt);

/// <summary>Ubicación en Blob + metadatos de un informe (para descarga/lectura del JSON).</summary>
public sealed record ReportBlobRef(string Container, string BlobName, string Status, bool IsPartial, string? SummaryJson, string? GeneratedBy);

/// <summary>Ítem del plan de acción editable (port de report_action_plan).</summary>
public sealed record ActionPlanItem(
    int ItemId, int Orden, string Prioridad, string Hallazgo, string? Accion,
    string? Responsable, string Estado, string? UpdatedBy, string UpdatedAt);

public sealed record ActionPlanWrite(int Orden, string Prioridad, string Hallazgo, string? Accion, string? Responsable, string Estado);

/// <summary>
/// Acceso al índice de informes (client_monthly_report) + plan de acción (report_action_plan).
/// Port de los accesos SQL de app/routes/reports.py. El JSON del informe lo guarda/lee el builder
/// vía Blob (BlobStorageService); aquí solo el índice liviano y el plan.
/// </summary>
public interface IReportStore
{
    Task<bool> ClientExistsAsync(int clientId, CancellationToken ct = default);
    Task<IReadOnlyList<ReportIndexItem>> ListAsync(int clientId, CancellationToken ct = default);
    Task<ReportBlobRef?> GetBlobRefAsync(int clientId, int year, int month, CancellationToken ct = default);
    Task UpsertPlaceholderAsync(int clientId, int year, int month, string container, string blobName, string? generatedBy, CancellationToken ct = default);
    Task MarkFailedAsync(int clientId, int year, int month, string errorMessage, CancellationToken ct = default);
    Task MarkCompletedAsync(int clientId, int year, int month, string container, string blobName, bool isPartial, string? summaryJson, string? generatedBy, CancellationToken ct = default);

    Task<IReadOnlyList<ActionPlanItem>> ListActionPlanAsync(int clientId, int year, int month, CancellationToken ct = default);
    Task<int> CountActionPlanAsync(int clientId, int year, int month, CancellationToken ct = default);
    Task<ActionPlanItem> CreateActionItemAsync(int clientId, int year, int month, ActionPlanWrite item, string? actor, CancellationToken ct = default);
    Task SeedActionPlanAsync(int clientId, int year, int month, IReadOnlyList<ActionPlanWrite> items, string? actor, CancellationToken ct = default);
    Task<ActionPlanItem?> UpdateActionItemAsync(int clientId, int year, int month, int itemId, ActionPlanWrite item, string? actor, CancellationToken ct = default);
    Task<bool> DeleteActionItemAsync(int clientId, int year, int month, int itemId, CancellationToken ct = default);
}

public sealed class SqlReportStore(ISqlConnectionFactory factory) : IReportStore
{
    private static string Dt(SqlDataReader r, int i)
    {
        if (r.IsDBNull(i)) return "";
        if (r.GetValue(i) is DateTime dt)
        {
            // str(datetime) de Python omite la fracción cuando es 0; si no, imprime 6 dígitos.
            // pyodbc entrega microsegundos (6 díg); DATETIME2 puede traer 7 → truncar a micro.
            var micro = (dt.Ticks % TimeSpan.TicksPerSecond) / 10;
            return micro == 0
                ? dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        }
        return r.GetValue(i).ToString() ?? "";
    }

    public async Task<bool> ClientExistsAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dbo.clients WHERE client_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", clientId));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<IReadOnlyList<ReportIndexItem>> ListAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await ReportsSchema.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT report_id, period_year, period_month, status, is_partial, summary_json, generated_by, generated_at
            FROM dbo.client_monthly_report WHERE client_id = @id ORDER BY period_year DESC, period_month DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@id", clientId));
        var list = new List<ReportIndexItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ReportIndexItem(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3),
                !r.IsDBNull(4) && r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), Dt(r, 7)));
        return list;
    }

    public async Task<ReportBlobRef?> GetBlobRefAsync(int clientId, int year, int month, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await ReportsSchema.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT blob_container, blob_name, status, is_partial, summary_json, generated_by FROM dbo.client_monthly_report
            WHERE client_id = @id AND period_year = @y AND period_month = @m
            """;
        AddPeriod(cmd, clientId, year, month);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ReportBlobRef(r.GetString(0), r.GetString(1), r.GetString(2), !r.IsDBNull(3) && r.GetBoolean(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));
    }

    public async Task UpsertPlaceholderAsync(int clientId, int year, int month, string container, string blobName, string? generatedBy, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await ReportsSchema.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF EXISTS (SELECT 1 FROM dbo.client_monthly_report WHERE client_id = @id AND period_year = @y AND period_month = @m)
                UPDATE dbo.client_monthly_report SET status = 'generating', blob_container = @c, blob_name = @b, summary_json = NULL, generated_by = @by, generated_at = SYSUTCDATETIME()
                WHERE client_id = @id AND period_year = @y AND period_month = @m;
            ELSE
                INSERT INTO dbo.client_monthly_report (client_id, period_year, period_month, blob_container, blob_name, status, generated_by)
                VALUES (@id, @y, @m, @c, @b, 'generating', @by);
            """;
        AddPeriod(cmd, clientId, year, month);
        cmd.Parameters.Add(new SqlParameter("@c", container));
        cmd.Parameters.Add(new SqlParameter("@b", blobName));
        cmd.Parameters.Add(new SqlParameter("@by", (object?)generatedBy ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(int clientId, int year, int month, string errorMessage, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.client_monthly_report SET status = 'failed', summary_json = @err, generated_at = SYSUTCDATETIME()
            WHERE client_id = @id AND period_year = @y AND period_month = @m
            """;
        AddPeriod(cmd, clientId, year, month);
        cmd.Parameters.Add(new SqlParameter("@err", System.Text.Json.JsonSerializer.Serialize(new { error = errorMessage })));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkCompletedAsync(int clientId, int year, int month, string container, string blobName, bool isPartial, string? summaryJson, string? generatedBy, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.client_monthly_report
            SET analysis_id = NULL, blob_container = @c, blob_name = @b, status = 'completed',
                is_partial = @partial, summary_json = @sum, generated_by = @by, generated_at = SYSUTCDATETIME()
            WHERE client_id = @id AND period_year = @y AND period_month = @m
            """;
        AddPeriod(cmd, clientId, year, month);
        cmd.Parameters.Add(new SqlParameter("@c", container));
        cmd.Parameters.Add(new SqlParameter("@b", blobName));
        cmd.Parameters.Add(new SqlParameter("@partial", isPartial ? 1 : 0));
        cmd.Parameters.Add(new SqlParameter("@sum", (object?)summaryJson ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@by", (object?)generatedBy ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------- plan de acción --------------------

    public async Task<IReadOnlyList<ActionPlanItem>> ListActionPlanAsync(int clientId, int year, int month, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await ReportsSchema.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT item_id, orden, prioridad, hallazgo, accion, responsable, estado, updated_by, updated_at
            FROM dbo.report_action_plan WHERE client_id = @id AND period_year = @y AND period_month = @m
            ORDER BY orden, item_id
            """;
        AddPeriod(cmd, clientId, year, month);
        var list = new List<ActionPlanItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(MapItem(r));
        return list;
    }

    public async Task<int> CountActionPlanAsync(int clientId, int year, int month, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await ReportsSchema.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dbo.report_action_plan WHERE client_id = @id AND period_year = @y AND period_month = @m";
        AddPeriod(cmd, clientId, year, month);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<ActionPlanItem> CreateActionItemAsync(int clientId, int year, int month, ActionPlanWrite item, string? actor, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await ReportsSchema.EnsureSchemaAsync(conn, ct);
        return await InsertItemAsync(conn, null, clientId, year, month, item, actor, ct);
    }

    public async Task SeedActionPlanAsync(int clientId, int year, int month, IReadOnlyList<ActionPlanWrite> items, string? actor, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await ReportsSchema.EnsureSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM dbo.report_action_plan WHERE client_id = @id AND period_year = @y AND period_month = @m";
            AddPeriod(del, clientId, year, month);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var item in items)
            await InsertItemAsync(conn, tx, clientId, year, month, item, actor, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<ActionPlanItem?> UpdateActionItemAsync(int clientId, int year, int month, int itemId, ActionPlanWrite item, string? actor, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await ReportsSchema.EnsureSchemaAsync(conn, ct);
        await using (var upd = conn.CreateCommand())
        {
            upd.CommandText = """
                UPDATE dbo.report_action_plan
                SET orden = @orden, prioridad = @prio, hallazgo = @hall, accion = @acc, responsable = @resp,
                    estado = @estado, updated_by = @by, updated_at = SYSUTCDATETIME()
                WHERE item_id = @item AND client_id = @id AND period_year = @y AND period_month = @m
                """;
            AddPeriod(upd, clientId, year, month);
            AddItemParams(upd, item, actor);
            upd.Parameters.Add(new SqlParameter("@item", itemId));
            if (await upd.ExecuteNonQueryAsync(ct) == 0) return null;
        }
        await using var get = conn.CreateCommand();
        get.CommandText = "SELECT item_id, orden, prioridad, hallazgo, accion, responsable, estado, updated_by, updated_at FROM dbo.report_action_plan WHERE item_id = @item";
        get.Parameters.Add(new SqlParameter("@item", itemId));
        await using var r = await get.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? MapItem(r) : null;
    }

    public async Task<bool> DeleteActionItemAsync(int clientId, int year, int month, int itemId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dbo.report_action_plan WHERE item_id = @item AND client_id = @id AND period_year = @y AND period_month = @m";
        AddPeriod(cmd, clientId, year, month);
        cmd.Parameters.Add(new SqlParameter("@item", itemId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // -------------------- helpers --------------------
    private static ActionPlanItem MapItem(SqlDataReader r) => new(
        r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
        r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), Dt(r, 8));

    private static async Task<ActionPlanItem> InsertItemAsync(SqlConnection conn, SqlTransaction? tx, int clientId, int year, int month, ActionPlanWrite item, string? actor, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO dbo.report_action_plan (client_id, period_year, period_month, orden, prioridad, hallazgo, accion, responsable, estado, updated_by)
            OUTPUT INSERTED.item_id, INSERTED.orden, INSERTED.prioridad, INSERTED.hallazgo, INSERTED.accion, INSERTED.responsable, INSERTED.estado, INSERTED.updated_by, INSERTED.updated_at
            VALUES (@id, @y, @m, @orden, @prio, @hall, @acc, @resp, @estado, @by)
            """;
        AddPeriod(cmd, clientId, year, month);
        AddItemParams(cmd, item, actor);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return MapItem(r);
    }

    private static void AddPeriod(SqlCommand cmd, int clientId, int year, int month)
    {
        cmd.Parameters.Add(new SqlParameter("@id", clientId));
        cmd.Parameters.Add(new SqlParameter("@y", year));
        cmd.Parameters.Add(new SqlParameter("@m", month));
    }

    private static void AddItemParams(SqlCommand cmd, ActionPlanWrite item, string? actor)
    {
        cmd.Parameters.Add(new SqlParameter("@orden", item.Orden));
        cmd.Parameters.Add(new SqlParameter("@prio", item.Prioridad));
        cmd.Parameters.Add(new SqlParameter("@hall", item.Hallazgo));
        cmd.Parameters.Add(new SqlParameter("@acc", (object?)item.Accion ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@resp", (object?)item.Responsable ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@estado", item.Estado));
        cmd.Parameters.Add(new SqlParameter("@by", (object?)actor ?? DBNull.Value));
    }
}
