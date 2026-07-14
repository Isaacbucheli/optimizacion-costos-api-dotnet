using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Identity;

/// <summary>Permiso de un rol sobre un módulo del catálogo (Modules.All).</summary>
public sealed record ModulePermission(string ModuleKey, bool CanView, bool CanEdit);

/// <summary>
/// Acceso a dbo.role_module_permission: la matriz rol×módulo que decide qué ve/edita
/// cada grupo (consultor/lector). Fila ausente = denegado. admin nunca consulta aquí.
/// </summary>
public interface IModulePermissionStore
{
    Task<IReadOnlyDictionary<string, ModulePermission>> GetForRoleAsync(string role, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, IReadOnlyList<ModulePermission>>> GetMatrixAsync(CancellationToken ct = default);
    Task ReplaceMatrixAsync(IReadOnlyDictionary<string, IReadOnlyList<ModulePermission>> matrix, string updatedBy, CancellationToken ct = default);
}

public sealed class SqlModulePermissionStore(ISqlConnectionFactory factory) : IModulePermissionStore
{
    // Crea la tabla si falta y la siembra SOLO si está vacía, espejando el comportamiento
    // previo a esta feature: consultor ve/edita todo; lector solo ve (optimization excluido,
    // que era Editors-only). Así el deploy no cambia nada hasta que el admin desmarque.
    private static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                IF OBJECT_ID('dbo.role_module_permission', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.role_module_permission (
                        role        NVARCHAR(30)  NOT NULL,
                        module_key  NVARCHAR(50)  NOT NULL,
                        can_view    BIT           NOT NULL CONSTRAINT DF_rmp_view DEFAULT 0,
                        can_edit    BIT           NOT NULL CONSTRAINT DF_rmp_edit DEFAULT 0,
                        updated_at  DATETIME2     NULL,
                        updated_by  NVARCHAR(255) NULL,
                        CONSTRAINT PK_role_module_permission PRIMARY KEY (role, module_key)
                    );
                END
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM dbo.role_module_permission";
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) > 0) return;
        }

        foreach (var m in Modules.All)
        {
            var lectorView = m.Key != Modules.Optimization;
            await using var seed = conn.CreateCommand();
            seed.CommandText = """
                INSERT INTO dbo.role_module_permission (role, module_key, can_view, can_edit)
                VALUES (@consultor, @key, 1, 1), (@lector, @key, @lectorView, 0)
                """;
            seed.Parameters.AddWithValue("@consultor", Roles.Consultor);
            seed.Parameters.AddWithValue("@lector", Roles.Lector);
            seed.Parameters.AddWithValue("@key", m.Key);
            seed.Parameters.AddWithValue("@lectorView", lectorView);
            await seed.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyDictionary<string, ModulePermission>> GetForRoleAsync(string role, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT module_key, can_view, can_edit FROM dbo.role_module_permission WHERE role = @role";
        cmd.Parameters.AddWithValue("@role", role.Trim().ToLowerInvariant());

        var result = new Dictionary<string, ModulePermission>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = reader.GetString(0);
            result[key] = new ModulePermission(key, reader.GetBoolean(1), reader.GetBoolean(2));
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ModulePermission>>> GetMatrixAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT role, module_key, can_view, can_edit FROM dbo.role_module_permission ORDER BY role, module_key";

        var acc = new Dictionary<string, List<ModulePermission>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var role = reader.GetString(0);
            if (!acc.TryGetValue(role, out var list)) acc[role] = list = [];
            list.Add(new ModulePermission(reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        }
        return acc.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ModulePermission>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    public async Task ReplaceMatrixAsync(IReadOnlyDictionary<string, IReadOnlyList<ModulePermission>> matrix, string updatedBy, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        foreach (var (role, rows) in matrix)
        {
            await using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM dbo.role_module_permission WHERE role = @role";
                del.Parameters.AddWithValue("@role", role);
                await del.ExecuteNonQueryAsync(ct);
            }
            foreach (var row in rows)
            {
                await using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO dbo.role_module_permission (role, module_key, can_view, can_edit, updated_at, updated_by)
                    VALUES (@role, @key, @view, @edit, SYSUTCDATETIME(), @by)
                    """;
                ins.Parameters.AddWithValue("@role", role);
                ins.Parameters.AddWithValue("@key", row.ModuleKey);
                ins.Parameters.AddWithValue("@view", row.CanView);
                ins.Parameters.AddWithValue("@edit", row.CanEdit);
                ins.Parameters.AddWithValue("@by", (object?)updatedBy ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
    }
}
