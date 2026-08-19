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
    private const string SelectSql = """
        SELECT email, full_name, role, is_active, tokens_revoked_at
        FROM dbo.app_users
        WHERE LOWER(email) = LOWER(@email)
        """;

    /// <summary>El mismo ALTER idempotente de EnsureAuthSchemaAsync (SqlAppUserStore).
    /// Vive duplicado aquí a propósito: este directory corre en CADA request autenticado
    /// y no puede depender de que el login (que es quien corre el ensure completo) haya
    /// pasado primero tras un deploy.</summary>
    private const string EnsureRevokedColumnSql = """
        IF COL_LENGTH('dbo.app_users', 'tokens_revoked_at') IS NULL
            ALTER TABLE dbo.app_users ADD tokens_revoked_at DATETIME2 NULL;
        """;

    public async Task<AppUser?> FindActiveByEmailAsync(string email, CancellationToken ct = default)
    {
        try
        {
            return await QueryAsync(email, ct);
        }
        catch (SqlException ex) when (ex.Number == 207)
        {
            // La columna tokens_revoked_at todavía no existe: request autenticado con un
            // token emitido antes del deploy, antes del primer login posterior (WEB-12).
            // Se crea la columna y se reintenta una sola vez; sin esta defensa, todos los
            // requests autenticados darían 500 hasta el primer login.
            await using var conn = await factory.OpenAsync(ct);
            await using var ensure = conn.CreateCommand();
            ensure.CommandText = EnsureRevokedColumnSql;
            await ensure.ExecuteNonQueryAsync(ct);
            return await QueryAsync(email, ct);
        }
    }

    private async Task<AppUser?> QueryAsync(string email, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectSql;
        cmd.Parameters.Add(new SqlParameter("@email", email));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var user = new AppUser(
            Email: reader.GetString(0),
            FullName: reader.GetString(1),
            Role: reader.GetString(2),
            IsActive: reader.GetBoolean(3),
            TokensRevokedAt: reader.IsDBNull(4) ? null : reader.GetDateTime(4));

        return user.IsActive ? user : null;
    }
}
