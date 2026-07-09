using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.PolicyCatalog;

/// <summary>
/// Implementacion SQL del catalogo de politicas. Asegura el schema y siembra de forma
/// perezosa (espejo de SqlAlertCatalogStore), por lo que el arranque no depende de la BD.
/// </summary>
public sealed class SqlPolicyCatalogStore(ISqlConnectionFactory factory) : IPolicyCatalogStore
{
    private const string PolicySelect = """
        SELECT policy_id, policy_number, name, category, policy_type, recommended_effect,
               mode, key_parameters, description, objective, recommended_scope, rollout,
               risk, example_parameters, azure_cli, powershell, script_notes,
               official_source, is_active
        FROM dbo.policy_catalog
        """;

    public async Task<IReadOnlyList<PolicyItem>> ListPoliciesAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        var where = includeInactive ? "" : "WHERE is_active = 1";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{PolicySelect} {where} ORDER BY policy_number, policy_id";

        var items = new List<PolicyItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(MapPolicy(reader));
        return items;
    }

    public async Task<PolicyItem?> GetPolicyAsync(int policyId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{PolicySelect} WHERE policy_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", policyId));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapPolicy(reader) : null;
    }

    public async Task<int> CreatePolicyAsync(PolicyCreate data, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PolicyCatalogSchema.EnsureSchemaAsync(conn, ct);

        var cols = PolicyColumns.Policy;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO dbo.policy_catalog ({string.Join(", ", cols)}) " +
            $"OUTPUT INSERTED.policy_id VALUES ({string.Join(", ", cols.Select((_, i) => $"@p{i}"))})";

        var values = new object?[]
        {
            data.PolicyNumber, data.Name, data.Category, data.PolicyType, data.RecommendedEffect,
            data.Mode, data.KeyParameters, data.Description, data.Objective, data.RecommendedScope,
            data.Rollout, data.Risk, data.ExampleParameters, data.AzureCli, data.Powershell,
            data.ScriptNotes, data.OfficialSource,
        };
        for (var i = 0; i < cols.Length; i++)
            cmd.Parameters.Add(new SqlParameter($"@p{i}", values[i] ?? DBNull.Value));

        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// UPDATE dinamico: solo columnas presentes en `fields` que esten en la whitelist.
    /// Los nombres de columna nunca vienen del usuario (se filtran contra la whitelist);
    /// los valores van siempre como parametros.
    /// </summary>
    public async Task<bool> UpdatePolicyAsync(int policyId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        var sets = new List<string>();
        var parameters = new List<SqlParameter>();
        var i = 0;
        foreach (var col in PolicyColumns.Policy)
        {
            if (!fields.TryGetValue(col, out var value)) continue;
            sets.Add($"{col} = @p{i}");
            parameters.Add(new SqlParameter($"@p{i}", value ?? DBNull.Value));
            i++;
        }
        if (sets.Count == 0) return false;

        sets.Add("updated_at = SYSUTCDATETIME()");
        await using var conn = await factory.OpenAsync(ct);
        await PolicyCatalogSchema.EnsureSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE dbo.policy_catalog SET {string.Join(", ", sets)} WHERE policy_id = @id";
        cmd.Parameters.AddRange(parameters.ToArray());
        cmd.Parameters.Add(new SqlParameter("@id", policyId));

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> SoftDeletePolicyAsync(int policyId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PolicyCatalogSchema.EnsureSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE dbo.policy_catalog SET is_active = 0, updated_at = SYSUTCDATETIME() WHERE policy_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", policyId));

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ---------- Helpers ----------

    private async Task PrepareAsync(SqlConnection conn, CancellationToken ct)
    {
        await PolicyCatalogSchema.EnsureSchemaAsync(conn, ct);
        await PolicyCatalogSchema.SeedDefaultsAsync(conn, ct);
    }

    private static PolicyItem MapPolicy(SqlDataReader r) => new()
    {
        PolicyId = r.GetInt32(0),
        PolicyNumber = r.IsDBNull(1) ? null : r.GetInt32(1),
        Name = r.GetString(2),
        Category = r.IsDBNull(3) ? null : r.GetString(3),
        PolicyType = r.IsDBNull(4) ? null : r.GetString(4),
        RecommendedEffect = r.IsDBNull(5) ? null : r.GetString(5),
        Mode = r.IsDBNull(6) ? null : r.GetString(6),
        KeyParameters = r.IsDBNull(7) ? null : r.GetString(7),
        Description = r.IsDBNull(8) ? null : r.GetString(8),
        Objective = r.IsDBNull(9) ? null : r.GetString(9),
        RecommendedScope = r.IsDBNull(10) ? null : r.GetString(10),
        Rollout = r.IsDBNull(11) ? null : r.GetString(11),
        Risk = r.IsDBNull(12) ? null : r.GetString(12),
        ExampleParameters = r.IsDBNull(13) ? null : r.GetString(13),
        AzureCli = r.IsDBNull(14) ? null : r.GetString(14),
        Powershell = r.IsDBNull(15) ? null : r.GetString(15),
        ScriptNotes = r.IsDBNull(16) ? null : r.GetString(16),
        OfficialSource = r.IsDBNull(17) ? null : r.GetString(17),
        IsActive = r.GetBoolean(18),
    };
}
