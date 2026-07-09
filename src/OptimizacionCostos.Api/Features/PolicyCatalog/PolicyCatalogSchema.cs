using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.PolicyCatalog;

/// <summary>
/// Crea la tabla (idempotente) y siembra la linea base BIT de Azure Policy solo si esta
/// vacia. Espejo de AlertCatalogSchema.
/// </summary>
public static class PolicyCatalogSchema
{
    private const string SeedResource =
        "OptimizacionCostos.Api.Features.PolicyCatalog.Seed.policy_seed.json";

    public static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await ExecAsync(conn, ct, """
            IF OBJECT_ID('dbo.policy_catalog', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.policy_catalog (
                    policy_id INT IDENTITY(1,1) PRIMARY KEY,
                    policy_number INT NULL,
                    name NVARCHAR(300) NOT NULL,
                    category NVARCHAR(160) NULL,
                    policy_type NVARCHAR(80) NULL,
                    recommended_effect NVARCHAR(60) NULL,
                    mode NVARCHAR(40) NULL,
                    key_parameters NVARCHAR(300) NULL,
                    description NVARCHAR(MAX) NULL,
                    objective NVARCHAR(MAX) NULL,
                    recommended_scope NVARCHAR(200) NULL,
                    rollout NVARCHAR(MAX) NULL,
                    risk NVARCHAR(MAX) NULL,
                    example_parameters NVARCHAR(MAX) NULL,
                    azure_cli NVARCHAR(MAX) NULL,
                    powershell NVARCHAR(MAX) NULL,
                    script_notes NVARCHAR(MAX) NULL,
                    official_source NVARCHAR(500) NULL,
                    is_active BIT NOT NULL CONSTRAINT DF_policy_catalog_active DEFAULT 1,
                    created_at DATETIME2 NOT NULL CONSTRAINT DF_policy_catalog_created DEFAULT SYSUTCDATETIME(),
                    updated_at DATETIME2 NULL
                );
            END
            """);
    }

    /// <summary>Inserta el seed estandar solo si la tabla esta vacia (idempotente).</summary>
    public static async Task SeedDefaultsAsync(SqlConnection conn, CancellationToken ct)
    {
        if (await CountAsync(conn, ct, "dbo.policy_catalog") != 0) return;

        using var doc = LoadSeed();
        var cols = PolicyColumns.Policy;
        var sql = $"INSERT INTO dbo.policy_catalog ({string.Join(", ", cols)}) " +
                  $"VALUES ({string.Join(", ", cols.Select((_, i) => $"@p{i}"))})";
        foreach (var policy in doc.RootElement.GetProperty("policies").EnumerateArray())
            await InsertSeedRowAsync(conn, ct, sql, cols, policy);
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
