using System.Globalization;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.CostEngine.Engine;

namespace OptimizacionCostos.Api.Features.Catalog;

/// <summary>Datos para crear un servicio del catálogo (port de ServiceCreate).</summary>
public sealed record CatalogEntryWrite(
    string ServiceKey, string DisplayName, string AzureResourceType, string ServiceCategory,
    string? DetailTableName, string InserterKey, string CalculatorKey, string KqlQuery,
    bool RiApplicable, string? RiFilterField, string? RiFilterValues, string? RiExcludeValues,
    bool AhbApplicable, bool RequiresManualCost, string? ExcelSheetName, int DisplayOrder,
    bool IsActive, string? Notes);

public sealed record DiscoveryForSuggest(string ResourceType, int ResourceCount, string? SampleNames);

/// <summary>
/// CRUD del catálogo de servicios (dbo.service_catalog) + auditoría de sugerencias IA. Port de
/// app/service_catalog.py + app/routes/service_catalog.py. Devuelve filas completas (20 columnas).
/// NOTA: no porta ensure_catalog_defaults (la BD clonada ya trae el catálogo sembrado).
/// </summary>
public interface IServiceCatalogAdmin
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAsync(bool onlyActive, bool includeInternal, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, object?>?> GetAsync(string serviceKey, CancellationToken ct = default);
    Task<bool> ExistsAsync(string serviceKey, CancellationToken ct = default);
    Task CreateAsync(CatalogEntryWrite w, CancellationToken ct = default);
    Task<bool> UpdateAsync(string serviceKey, IReadOnlyList<(string Col, object? Val)> sets, CancellationToken ct = default);
    Task<bool> SoftDeleteAsync(string serviceKey, CancellationToken ct = default);
    Task<DiscoveryForSuggest?> GetDiscoveryAsync(int discoveryId, CancellationToken ct = default);
    Task InsertAiSuggestionAuditAsync(int discoveryId, string resourceType, string suggestionJson, string? reasonEs, CancellationToken ct = default);
}

public sealed class SqlServiceCatalogAdmin(ISqlConnectionFactory factory) : IServiceCatalogAdmin
{
    private const string SelectCols = """
        service_key, display_name, azure_resource_type, service_category,
        detail_table_name, inserter_key, calculator_key, kql_query,
        ri_applicable, ri_filter_field, ri_filter_values, ri_exclude_values,
        ahb_applicable, requires_manual_cost, excel_sheet_name, display_order,
        is_active, notes, created_at, updated_at
        """;

    private static string? Dt(SqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetValue(i) is DateTime dt
            ? dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) : r.GetValue(i).ToString();

    private static IReadOnlyDictionary<string, object?> Map(SqlDataReader r)
    {
        var serviceKey = r.GetString(0);
        return new Dictionary<string, object?>
        {
            ["service_key"] = serviceKey,
            ["display_name"] = r.IsDBNull(1) ? null : r.GetString(1),
            ["azure_resource_type"] = r.IsDBNull(2) ? null : r.GetString(2),
            ["service_category"] = r.IsDBNull(3) ? null : r.GetString(3),
            ["detail_table_name"] = r.IsDBNull(4) ? null : r.GetString(4),
            ["inserter_key"] = r.IsDBNull(5) ? null : r.GetString(5),
            ["calculator_key"] = r.IsDBNull(6) ? null : r.GetString(6),
            ["kql_query"] = r.IsDBNull(7) ? null : r.GetString(7),
            ["ri_applicable"] = !r.IsDBNull(8) && r.GetBoolean(8),
            ["ri_filter_field"] = r.IsDBNull(9) ? null : r.GetString(9),
            ["ri_filter_values"] = r.IsDBNull(10) ? null : r.GetString(10),
            ["ri_exclude_values"] = r.IsDBNull(11) ? null : r.GetString(11),
            ["ahb_applicable"] = !r.IsDBNull(12) && r.GetBoolean(12),
            ["requires_manual_cost"] = !r.IsDBNull(13) && r.GetBoolean(13),
            ["excel_sheet_name"] = r.IsDBNull(14) ? null : r.GetString(14),
            ["display_order"] = r.IsDBNull(15) ? null : r.GetInt32(15),
            ["is_active"] = !r.IsDBNull(16) && r.GetBoolean(16),
            ["notes"] = r.IsDBNull(17) ? null : r.GetString(17),
            ["is_internal"] = ServiceDependencies.InternalServiceKeys.Contains(serviceKey),
            ["created_at"] = Dt(r, 18),
            ["updated_at"] = Dt(r, 19),
        };
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAsync(bool onlyActive, bool includeInternal, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectCols} FROM dbo.service_catalog {(onlyActive ? "WHERE is_active = 1" : "")} ORDER BY display_order, service_key";
        var list = new List<IReadOnlyDictionary<string, object?>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var row = Map(r);
            if (!includeInternal && (bool)row["is_internal"]!) continue;
            list.Add(row);
        }
        return list;
    }

    public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(string serviceKey, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectCols} FROM dbo.service_catalog WHERE service_key = @k";
        cmd.Parameters.Add(new SqlParameter("@k", serviceKey));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task<bool> ExistsAsync(string serviceKey, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dbo.service_catalog WHERE service_key = @k";
        cmd.Parameters.Add(new SqlParameter("@k", serviceKey));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task CreateAsync(CatalogEntryWrite w, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.service_catalog (
                service_key, display_name, azure_resource_type, service_category,
                detail_table_name, inserter_key, calculator_key, kql_query,
                ri_applicable, ri_filter_field, ri_filter_values, ri_exclude_values,
                ahb_applicable, requires_manual_cost, excel_sheet_name, display_order, is_active, notes)
            VALUES (@key, @dn, @art, @cat, @dtn, @ik, @ck, @kql, @ri, @rff, @rfv, @rev, @ahb, @rmc, @esn, @do, @act, @notes)
            """;
        void P(string n, object? v) => cmd.Parameters.Add(new SqlParameter(n, v ?? DBNull.Value));
        P("@key", w.ServiceKey); P("@dn", w.DisplayName); P("@art", w.AzureResourceType); P("@cat", w.ServiceCategory);
        P("@dtn", w.DetailTableName); P("@ik", w.InserterKey); P("@ck", w.CalculatorKey); P("@kql", w.KqlQuery);
        P("@ri", w.RiApplicable ? 1 : 0); P("@rff", w.RiFilterField); P("@rfv", w.RiFilterValues); P("@rev", w.RiExcludeValues);
        P("@ahb", w.AhbApplicable ? 1 : 0); P("@rmc", w.RequiresManualCost ? 1 : 0); P("@esn", w.ExcelSheetName);
        P("@do", w.DisplayOrder); P("@act", w.IsActive ? 1 : 0); P("@notes", w.Notes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> UpdateAsync(string serviceKey, IReadOnlyList<(string Col, object? Val)> sets, CancellationToken ct = default)
    {
        if (sets.Count == 0) return false;
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var assignments = sets.Select((s, i) => $"{s.Col} = @p{i}").ToList();
        assignments.Add("updated_at = SYSUTCDATETIME()");
        cmd.CommandText = $"UPDATE dbo.service_catalog SET {string.Join(", ", assignments)} WHERE service_key = @key";
        for (var i = 0; i < sets.Count; i++)
            cmd.Parameters.Add(new SqlParameter($"@p{i}", sets[i].Val ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@key", serviceKey));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> SoftDeleteAsync(string serviceKey, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dbo.service_catalog SET is_active = 0, updated_at = SYSUTCDATETIME() WHERE service_key = @k";
        cmd.Parameters.Add(new SqlParameter("@k", serviceKey));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<DiscoveryForSuggest?> GetDiscoveryAsync(int discoveryId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureAiAuditSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT resource_type, resource_count, sample_names
            FROM dbo.discovered_service_types WHERE discovery_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", discoveryId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new DiscoveryForSuggest(r.GetString(0), r.IsDBNull(1) ? 0 : r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    public async Task InsertAiSuggestionAuditAsync(int discoveryId, string resourceType, string suggestionJson, string? reasonEs, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureAiAuditSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.catalog_ai_suggestion_audit (discovery_id, resource_type, suggestion_json, reason_es)
            VALUES (@id, @rt, @sj, @reason)
            """;
        cmd.Parameters.Add(new SqlParameter("@id", discoveryId));
        cmd.Parameters.Add(new SqlParameter("@rt", resourceType));
        cmd.Parameters.Add(new SqlParameter("@sj", suggestionJson));
        cmd.Parameters.Add(new SqlParameter("@reason", (object?)reasonEs ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureAiAuditSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.catalog_ai_suggestion_audit', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.catalog_ai_suggestion_audit (
                    audit_id INT IDENTITY(1,1) PRIMARY KEY,
                    discovery_id INT NOT NULL, resource_type NVARCHAR(300) NOT NULL,
                    suggestion_json NVARCHAR(MAX) NULL, reason_es NVARCHAR(MAX) NULL,
                    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                );
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
