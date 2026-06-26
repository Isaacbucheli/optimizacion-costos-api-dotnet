using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Auth;

/// <summary>
/// Lee dbo.app_users. Equivale a la consulta de require_request_auth en el FastAPI:
/// el rol del token NO se usa para autorizar; manda el rol vivo en BD.
/// SQL siempre parametrizado.
/// </summary>
public sealed class SqlUserDirectory(ISqlConnectionFactory factory) : IUserDirectory
{
    public async Task<AppUser?> FindActiveByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT email, full_name, role, is_active
            FROM dbo.app_users
            WHERE LOWER(email) = LOWER(@email)
            """;
        cmd.Parameters.Add(new SqlParameter("@email", email));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var user = new AppUser(
            Email: reader.GetString(0),
            FullName: reader.GetString(1),
            Role: reader.GetString(2),
            IsActive: reader.GetBoolean(3));

        return user.IsActive ? user : null;
    }
}
