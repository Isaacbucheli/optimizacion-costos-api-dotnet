using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.AlertCatalog;

/// <summary>
/// Implementacion SQL del catalogo. Asegura el schema y siembra de forma perezosa
/// (como _prepare en el FastAPI), por lo que el arranque no depende de la BD.
/// </summary>
public sealed class SqlAlertCatalogStore(ISqlConnectionFactory factory) : IAlertCatalogStore
{
    private const string AlertSelect = """
        SELECT alert_id, alert_number, name, resource, alert_type, description,
               severity, origin, detail, action_group, kql_code,
               technical_requirement, is_active
        FROM dbo.alert_catalog
        """;

    private const string KqlSelect =
        "SELECT kql_id, name, description, kql_query, is_active FROM dbo.alert_kql_library";

    // ---------- Alertas ----------

    public async Task<IReadOnlyList<AlertItem>> ListAlertsAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        var where = includeInactive ? "" : "WHERE is_active = 1";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{AlertSelect} {where} ORDER BY alert_number, alert_id";

        var items = new List<AlertItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(MapAlert(reader));
        return items;
    }

    public async Task<AlertItem?> GetAlertAsync(int alertId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{AlertSelect} WHERE alert_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", alertId));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapAlert(reader) : null;
    }

    public async Task<int> CreateAlertAsync(AlertCreate data, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await AlertCatalogSchema.EnsureSchemaAsync(conn, ct);

        var cols = AlertColumns.Alert;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO dbo.alert_catalog ({string.Join(", ", cols)}) " +
            $"OUTPUT INSERTED.alert_id VALUES ({string.Join(", ", cols.Select((_, i) => $"@p{i}"))})";

        var values = new object?[]
        {
            data.AlertNumber, data.Name, data.Resource, data.AlertType, data.Description,
            data.Severity, data.Origin, data.Detail, data.ActionGroup, data.KqlCode,
            data.TechnicalRequirement,
        };
        for (var i = 0; i < cols.Length; i++)
            cmd.Parameters.Add(new SqlParameter($"@p{i}", values[i] ?? DBNull.Value));

        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public Task<bool> UpdateAlertAsync(int alertId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
        => UpdateAsync("dbo.alert_catalog", "alert_id", AlertColumns.Alert, alertId, fields, ct);

    public Task<bool> SoftDeleteAlertAsync(int alertId, CancellationToken ct = default)
        => SoftDeleteAsync("dbo.alert_catalog", "alert_id", alertId, ct);

    // ---------- KQL ----------

    public async Task<IReadOnlyList<KqlItem>> ListKqlAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        var where = includeInactive ? "" : "WHERE is_active = 1";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{KqlSelect} {where} ORDER BY name";

        var items = new List<KqlItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(MapKql(reader));
        return items;
    }

    public async Task<KqlItem?> GetKqlAsync(int kqlId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{KqlSelect} WHERE kql_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", kqlId));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapKql(reader) : null;
    }

    public async Task<int> CreateKqlAsync(KqlCreate data, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await AlertCatalogSchema.EnsureSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO dbo.alert_kql_library (name, description, kql_query) " +
            "OUTPUT INSERTED.kql_id VALUES (@name, @description, @kql_query)";
        cmd.Parameters.Add(new SqlParameter("@name", data.Name));
        cmd.Parameters.Add(new SqlParameter("@description", (object?)data.Description ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@kql_query", (object?)data.KqlQuery ?? DBNull.Value));

        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public Task<bool> UpdateKqlAsync(int kqlId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
        => UpdateAsync("dbo.alert_kql_library", "kql_id", AlertColumns.Kql, kqlId, fields, ct);

    public Task<bool> SoftDeleteKqlAsync(int kqlId, CancellationToken ct = default)
        => SoftDeleteAsync("dbo.alert_kql_library", "kql_id", kqlId, ct);

    // ---------- Helpers ----------

    private async Task PrepareAsync(SqlConnection conn, CancellationToken ct)
    {
        await AlertCatalogSchema.EnsureSchemaAsync(conn, ct);
        await AlertCatalogSchema.SeedDefaultsAsync(conn, ct);
    }

    /// <summary>
    /// UPDATE dinamico: solo columnas presentes en `fields` que esten en la whitelist
    /// `allowed`. Los nombres de columna nunca vienen del usuario (se filtran contra
    /// la whitelist); los valores van siempre como parametros.
    /// </summary>
    private async Task<bool> UpdateAsync(
        string table, string idCol, string[] allowed, int id,
        IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
    {
        var sets = new List<string>();
        var parameters = new List<SqlParameter>();
        var i = 0;
        foreach (var col in allowed)
        {
            if (!fields.TryGetValue(col, out var value)) continue;
            sets.Add($"{col} = @p{i}");
            parameters.Add(new SqlParameter($"@p{i}", value ?? DBNull.Value));
            i++;
        }
        if (sets.Count == 0) return false;

        sets.Add("updated_at = SYSUTCDATETIME()");
        await using var conn = await factory.OpenAsync(ct);
        await AlertCatalogSchema.EnsureSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {table} SET {string.Join(", ", sets)} WHERE {idCol} = @id";
        cmd.Parameters.AddRange(parameters.ToArray());
        cmd.Parameters.Add(new SqlParameter("@id", id));

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private async Task<bool> SoftDeleteAsync(string table, string idCol, int id, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await AlertCatalogSchema.EnsureSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"UPDATE {table} SET is_active = 0, updated_at = SYSUTCDATETIME() WHERE {idCol} = @id";
        cmd.Parameters.Add(new SqlParameter("@id", id));

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static AlertItem MapAlert(SqlDataReader r) => new()
    {
        AlertId = r.GetInt32(0),
        AlertNumber = r.IsDBNull(1) ? null : r.GetInt32(1),
        Name = r.GetString(2),
        Resource = r.IsDBNull(3) ? null : r.GetString(3),
        AlertType = r.IsDBNull(4) ? null : r.GetString(4),
        Description = r.IsDBNull(5) ? null : r.GetString(5),
        Severity = r.IsDBNull(6) ? null : r.GetString(6),
        Origin = r.IsDBNull(7) ? null : r.GetString(7),
        Detail = r.IsDBNull(8) ? null : r.GetString(8),
        ActionGroup = r.IsDBNull(9) ? null : r.GetString(9),
        KqlCode = r.IsDBNull(10) ? null : r.GetString(10),
        TechnicalRequirement = r.IsDBNull(11) ? null : r.GetString(11),
        IsActive = r.GetBoolean(12),
    };

    private static KqlItem MapKql(SqlDataReader r) => new()
    {
        KqlId = r.GetInt32(0),
        Name = r.GetString(1),
        Description = r.IsDBNull(2) ? null : r.GetString(2),
        KqlQuery = r.IsDBNull(3) ? null : r.GetString(3),
        IsActive = r.GetBoolean(4),
    };
}
