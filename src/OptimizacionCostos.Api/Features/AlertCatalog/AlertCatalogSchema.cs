using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.AlertCatalog;

/// <summary>
/// Crea las tablas (idempotente) y siembra el catalogo por defecto solo si estan
/// vacias. Equivale a app/alerts/schema.py + seed_alert_catalog_defaults().
/// </summary>
public static class AlertCatalogSchema
{
    private const string SeedResource =
        "OptimizacionCostos.Api.Features.AlertCatalog.Seed.alert_seed.json";

    public static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await ExecAsync(conn, ct, """
            IF OBJECT_ID('dbo.alert_catalog', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.alert_catalog (
                    alert_id INT IDENTITY(1,1) PRIMARY KEY,
                    alert_number INT NULL,
                    name NVARCHAR(300) NOT NULL,
                    resource NVARCHAR(120) NULL,
                    alert_type NVARCHAR(80) NULL,
                    description NVARCHAR(MAX) NULL,
                    severity NVARCHAR(40) NULL,
                    origin NVARCHAR(120) NULL,
                    detail NVARCHAR(MAX) NULL,
                    action_group NVARCHAR(300) NULL,
                    kql_code NVARCHAR(MAX) NULL,
                    technical_requirement NVARCHAR(MAX) NULL,
                    is_active BIT NOT NULL CONSTRAINT DF_alert_catalog_active DEFAULT 1,
                    created_at DATETIME2 NOT NULL CONSTRAINT DF_alert_catalog_created DEFAULT SYSUTCDATETIME(),
                    updated_at DATETIME2 NULL
                );
            END
            """);

        await ExecAsync(conn, ct, """
            IF OBJECT_ID('dbo.alert_kql_library', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.alert_kql_library (
                    kql_id INT IDENTITY(1,1) PRIMARY KEY,
                    name NVARCHAR(200) NOT NULL,
                    description NVARCHAR(MAX) NULL,
                    kql_query NVARCHAR(MAX) NULL,
                    is_active BIT NOT NULL CONSTRAINT DF_alert_kql_active DEFAULT 1,
                    created_at DATETIME2 NOT NULL CONSTRAINT DF_alert_kql_created DEFAULT SYSUTCDATETIME(),
                    updated_at DATETIME2 NULL
                );
            END
            """);
    }

    /// <summary>Inserta el seed estandar solo si las tablas estan vacias (idempotente).</summary>
    public static async Task SeedDefaultsAsync(SqlConnection conn, CancellationToken ct)
    {
        using var doc = LoadSeed();
        var root = doc.RootElement;

        if (await CountAsync(conn, ct, "dbo.alert_catalog") == 0)
        {
            var cols = AlertColumns.Alert;
            var sql = $"INSERT INTO dbo.alert_catalog ({string.Join(", ", cols)}) " +
                      $"VALUES ({string.Join(", ", cols.Select((_, i) => $"@p{i}"))})";
            foreach (var alert in root.GetProperty("alerts").EnumerateArray())
                await InsertSeedRowAsync(conn, ct, sql, cols, alert);
        }

        if (await CountAsync(conn, ct, "dbo.alert_kql_library") == 0)
        {
            var cols = AlertColumns.Kql;
            var sql = $"INSERT INTO dbo.alert_kql_library ({string.Join(", ", cols)}) " +
                      $"VALUES ({string.Join(", ", cols.Select((_, i) => $"@p{i}"))})";
            foreach (var kql in root.GetProperty("kql").EnumerateArray())
                await InsertSeedRowAsync(conn, ct, sql, cols, kql);
        }
    }

    private static async Task InsertSeedRowAsync(
        SqlConnection conn, CancellationToken ct, string sql, string[] cols, JsonElement row)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < cols.Length; i++)
        {
            var value = row.TryGetProperty(cols[i], out var el) ? JsonToDb(el) : null;
            cmd.Parameters.Add(new SqlParameter($"@p{i}", value ?? DBNull.Value));
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static object? JsonToDb(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt32(out var n) ? n : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => el.GetRawText(),
    };

    private static JsonDocument LoadSeed()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(SeedResource)
            ?? throw new InvalidOperationException($"Seed resource not found: {SeedResource}");
        return JsonDocument.Parse(stream);
    }

    private static async Task<int> CountAsync(SqlConnection conn, CancellationToken ct, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task ExecAsync(SqlConnection conn, CancellationToken ct, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
