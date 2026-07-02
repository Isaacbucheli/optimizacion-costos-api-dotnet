using System.Data;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.FinOpsData;

/// <summary>
/// Persistencia de los datasets open data (schema-lazy, patrón del repo). Reemplazo
/// atómico por dataset: DELETE + INSERT en una transacción.
/// Fuente de los datos: Microsoft FinOps Toolkit (MIT) — github.com/microsoft/finops-toolkit
/// </summary>
public sealed class SqlFinOpsDataStore(ISqlConnectionFactory factory) : IFinOpsDataStore
{
    public async Task ReplaceDatasetAsync(string datasetKey, IReadOnlyList<string[]> rows, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        var (table, insertSql, mapper) = DatasetSql(datasetKey);
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM dbo.{table}";
            await del.ExecuteNonQueryAsync(ct);
        }

        if (datasetKey == "commitment_eligibility")
        {
            await BulkInsertEligibilityAsync(conn, tx, rows, ct);
        }
        else
        {
            foreach (var row in rows)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = insertSql;
                mapper(cmd, row);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// commitment_eligibility trae ~121k filas: el INSERT fila-a-fila tarda 1-3 min contra Azure SQL
    /// (rompe el presupuesto de 30s del refresh-on-calculate y arriesga el timeout del gateway del
    /// App Service en el refresh manual). SqlBulkCopy dentro de la MISMA transacción mantiene la
    /// semántica atómica DELETE+INSERT.
    /// </summary>
    private static async Task BulkInsertEligibilityAsync(
        SqlConnection conn, SqlTransaction tx, IReadOnlyList<string[]> rows, CancellationToken ct)
    {
        var table = new DataTable();
        table.Columns.Add("meter_id", typeof(string));
        table.Columns.Add("ri_eligible", typeof(bool));
        table.Columns.Add("sp_eligible", typeof(bool));
        foreach (var r in rows)
        {
            var (ri, sp) = MapEligibility(r[1], r[2]);
            table.Rows.Add(r[0], ri, sp);
        }

        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = "dbo.finops_commitment_eligibility",
            BatchSize = 5000,
        };
        bulk.ColumnMappings.Add("meter_id", "meter_id");
        bulk.ColumnMappings.Add("ri_eligible", "ri_eligible");
        bulk.ColumnMappings.Add("sp_eligible", "sp_eligible");
        await bulk.WriteToServerAsync(table, ct);
    }

    public async Task RecordRefreshAsync(string datasetKey, int rowCount, string status, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            MERGE dbo.finops_data_refresh AS t
            USING (SELECT @d AS dataset) AS s ON t.dataset = s.dataset
            WHEN MATCHED THEN UPDATE SET refreshed_at = SYSUTCDATETIME(), row_count = @c, status = @s
            WHEN NOT MATCHED THEN INSERT (dataset, refreshed_at, row_count, status)
                VALUES (@d, SYSUTCDATETIME(), @c, @s);
            """;
        cmd.Parameters.Add(new SqlParameter("@d", datasetKey));
        cmd.Parameters.Add(new SqlParameter("@c", rowCount));
        cmd.Parameters.Add(new SqlParameter("@s", status));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<FinOpsRefreshStatus>> GetStatusAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT dataset, refreshed_at, row_count, status FROM dbo.finops_data_refresh";
        var list = new List<FinOpsRefreshStatus>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new FinOpsRefreshStatus(
                r.GetString(0),
                r.IsDBNull(1) ? null : DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc),
                r.IsDBNull(2) ? null : r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    private static (string Table, string InsertSql, Action<SqlCommand, string[]> Mapper) DatasetSql(string key) => key switch
    {
        "pricing_units" => ("finops_pricing_units",
            "INSERT INTO dbo.finops_pricing_units (unit_of_measure, account_types, pricing_block_size, distinct_units) VALUES (@0, @1, @2, @3)",
            (cmd, r) => AddParams(cmd, r[0], r[1], ParseDouble(r[2]), r[3])),
        "regions" => ("finops_regions",
            "INSERT INTO dbo.finops_regions (original_value, region_id, region_name) VALUES (@0, @1, @2)",
            (cmd, r) => AddParams(cmd, r[0].ToLowerInvariant(), r[1], r[2])),
        "services" => ("finops_services",
            "INSERT INTO dbo.finops_services (consumed_service, resource_type, service_name, service_category, service_subcategory, publisher_name, publisher_type, environment, service_model) VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8)",
            (cmd, r) => AddParams(cmd, r[0].ToLowerInvariant(), r[1].ToLowerInvariant(), r[2], r[3], At(r, 4), At(r, 5), At(r, 6), At(r, 7), At(r, 8))),
        "resource_types" => ("finops_resource_types",
            "INSERT INTO dbo.finops_resource_types (resource_type, singular_display_name, plural_display_name, is_preview, description, icon) VALUES (@0, @1, @2, @3, @4, @5)",
            (cmd, r) => AddParams(cmd, r[0].ToLowerInvariant(), r[1], r[2],
                string.Equals(At(r, 5), "true", StringComparison.OrdinalIgnoreCase) ? 1 : 0, At(r, 6), At(r, 7))),
        "commitment_eligibility" => ("finops_commitment_eligibility",
            "INSERT INTO dbo.finops_commitment_eligibility (meter_id, ri_eligible, sp_eligible) VALUES (@0, @1, @2)",
            (cmd, r) =>
            {
                var (ri, sp) = MapEligibility(r[1], r[2]);
                AddParams(cmd, r[0], ri ? 1 : 0, sp ? 1 : 0);
            }),
        _ => throw new ArgumentException($"Dataset desconocido: {key}"),
    };

    /// <summary>
    /// Spend = RI, Usage = SP — VALIDADO empíricamente 2026-07-02 (ver RiEligibility).
    /// El naming del dataset es contrario a la convención FOCUS; NO "corregirlo".
    /// </summary>
    internal static (bool Ri, bool Sp) MapEligibility(string spend, string usage) => (
        string.Equals(spend, "Eligible", StringComparison.OrdinalIgnoreCase),
        string.Equals(usage, "Eligible", StringComparison.OrdinalIgnoreCase));

    private static string? At(string[] row, int i) => i < row.Length && row[i].Length > 0 ? row[i] : null;
    private static object ParseDouble(string s) =>
        double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : DBNull.Value;
    private static void AddParams(SqlCommand cmd, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
            cmd.Parameters.Add(new SqlParameter($"@{i}", values[i] ?? DBNull.Value));
    }

    private static bool _schemaEnsured;
    internal static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        if (_schemaEnsured) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.finops_pricing_units', 'U') IS NULL
                CREATE TABLE dbo.finops_pricing_units (
                    unit_of_measure NVARCHAR(100) NOT NULL PRIMARY KEY,
                    account_types NVARCHAR(50) NULL,
                    pricing_block_size FLOAT NULL,
                    distinct_units NVARCHAR(100) NULL);
            IF OBJECT_ID('dbo.finops_regions', 'U') IS NULL
                CREATE TABLE dbo.finops_regions (
                    original_value NVARCHAR(100) NOT NULL PRIMARY KEY,
                    region_id NVARCHAR(100) NULL,
                    region_name NVARCHAR(100) NULL);
            IF OBJECT_ID('dbo.finops_services', 'U') IS NULL
                CREATE TABLE dbo.finops_services (
                    consumed_service NVARCHAR(200) NOT NULL,
                    resource_type NVARCHAR(200) NOT NULL,
                    service_name NVARCHAR(200) NULL,
                    service_category NVARCHAR(100) NULL,
                    service_subcategory NVARCHAR(100) NULL,
                    publisher_name NVARCHAR(100) NULL,
                    publisher_type NVARCHAR(50) NULL,
                    environment NVARCHAR(50) NULL,
                    service_model NVARCHAR(50) NULL,
                    CONSTRAINT PK_finops_services PRIMARY KEY (consumed_service, resource_type));
            IF OBJECT_ID('dbo.finops_resource_types', 'U') IS NULL
                CREATE TABLE dbo.finops_resource_types (
                    resource_type NVARCHAR(200) NOT NULL PRIMARY KEY,
                    singular_display_name NVARCHAR(200) NULL,
                    plural_display_name NVARCHAR(200) NULL,
                    is_preview BIT NOT NULL DEFAULT 0,
                    description NVARCHAR(MAX) NULL,
                    icon NVARCHAR(500) NULL);
            IF OBJECT_ID('dbo.finops_commitment_eligibility', 'U') IS NULL
                CREATE TABLE dbo.finops_commitment_eligibility (
                    meter_id NVARCHAR(100) NOT NULL PRIMARY KEY,
                    ri_eligible BIT NOT NULL,
                    sp_eligible BIT NOT NULL);
            IF OBJECT_ID('dbo.finops_data_refresh', 'U') IS NULL
                CREATE TABLE dbo.finops_data_refresh (
                    dataset NVARCHAR(50) NOT NULL PRIMARY KEY,
                    refreshed_at DATETIME2 NULL,
                    row_count INT NULL,
                    status NVARCHAR(400) NULL);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _schemaEnsured = true;
    }
}
