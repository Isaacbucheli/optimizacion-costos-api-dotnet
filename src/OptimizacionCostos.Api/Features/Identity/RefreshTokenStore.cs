using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Identity;

/// <summary>Fila de dbo.auth_refresh_tokens. Los DATETIME2 son UTC.</summary>
public sealed record RefreshTokenRow(
    long RefreshId,
    int UserId,
    Guid FamilyId,
    DateTime IssuedAt,
    DateTime ExpiresAt,
    DateTime? UsedAt,
    DateTime? RevokedAt);

/// <summary>
/// Refresh tokens rotatorios (spec DAST 2026-08-19). El token es opaco (32 bytes aleatorios,
/// base64url) y en BD solo vive su SHA-256. Cada login abre una FAMILIA con expiración
/// absoluta (AuthRefreshHours); cada canje marca el token como usado e inserta un hijo en la
/// misma familia con el MISMO expires_at (renovar no extiende la jornada). El reuso de un
/// token ya usado fuera de la gracia revoca la familia completa (detección de robo).
/// </summary>
public interface IRefreshTokenStore
{
    Task<RefreshTokenRow?> FindByHashAsync(byte[] hash, CancellationToken ct = default);
    Task CreateAsync(int userId, Guid familyId, byte[] hash, DateTime issuedAt, DateTime expiresAt, CancellationToken ct = default);
    Task MarkUsedAsync(long refreshId, DateTime usedAt, CancellationToken ct = default);
    Task RevokeFamilyAsync(Guid familyId, DateTime revokedAt, CancellationToken ct = default);
    Task RevokeAllForUserAsync(int userId, DateTime revokedAt, CancellationToken ct = default);
    /// <summary>Purga oportunista: borra hasta 100 filas vencidas hace más de una semana del
    /// propio usuario. Corre en cada canje; no hay job aparte.</summary>
    Task PurgeExpiredAsync(int userId, DateTime cutoff, CancellationToken ct = default);
}

/// <summary>Generación y hash del refresh token opaco (nunca un JWT).</summary>
public static class RefreshTokenCodec
{
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

public sealed class SqlRefreshTokenStore(ISqlConnectionFactory factory) : IRefreshTokenStore
{
    // DDL idempotente al estilo de la casa: corre al inicio de cada operación (lazy), no hay
    // migraciones. BINARY(32) = SHA-256; sin FK a app_users porque user_id puede no ser
    // IDENTITY en esquemas legacy de producción (mismo criterio que user_client_assignment).
    private const string EnsureSchemaSql = """
        IF OBJECT_ID('dbo.auth_refresh_tokens', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.auth_refresh_tokens (
                refresh_id  BIGINT IDENTITY(1,1) PRIMARY KEY,
                user_id     INT NOT NULL,
                family_id   UNIQUEIDENTIFIER NOT NULL,
                token_hash  BINARY(32) NOT NULL,
                issued_at   DATETIME2 NOT NULL,
                expires_at  DATETIME2 NOT NULL,
                used_at     DATETIME2 NULL,
                revoked_at  DATETIME2 NULL
            );
            CREATE UNIQUE INDEX UX_auth_refresh_tokens_hash ON dbo.auth_refresh_tokens(token_hash);
            CREATE INDEX IX_auth_refresh_tokens_user ON dbo.auth_refresh_tokens(user_id)
                INCLUDE (family_id, revoked_at, expires_at);
        END
        """;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = EnsureSchemaSql;
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }

    public async Task<RefreshTokenRow?> FindByHashAsync(byte[] hash, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT refresh_id, user_id, family_id, issued_at, expires_at, used_at, revoked_at
            FROM dbo.auth_refresh_tokens WHERE token_hash = @hash
            """;
        cmd.Parameters.Add(new SqlParameter("@hash", System.Data.SqlDbType.Binary, 32) { Value = hash });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new RefreshTokenRow(
            r.GetInt64(0), r.GetInt32(1), r.GetGuid(2), r.GetDateTime(3), r.GetDateTime(4),
            r.IsDBNull(5) ? null : r.GetDateTime(5),
            r.IsDBNull(6) ? null : r.GetDateTime(6));
    }

    public async Task CreateAsync(int userId, Guid familyId, byte[] hash, DateTime issuedAt, DateTime expiresAt, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.auth_refresh_tokens (user_id, family_id, token_hash, issued_at, expires_at)
            VALUES (@user, @family, @hash, @issued, @expires)
            """;
        cmd.Parameters.Add(new SqlParameter("@user", userId));
        cmd.Parameters.Add(new SqlParameter("@family", familyId));
        cmd.Parameters.Add(new SqlParameter("@hash", System.Data.SqlDbType.Binary, 32) { Value = hash });
        cmd.Parameters.Add(new SqlParameter("@issued", System.Data.SqlDbType.DateTime2) { Value = issuedAt });
        cmd.Parameters.Add(new SqlParameter("@expires", System.Data.SqlDbType.DateTime2) { Value = expiresAt });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkUsedAsync(long refreshId, DateTime usedAt, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dbo.auth_refresh_tokens SET used_at = @used WHERE refresh_id = @id AND used_at IS NULL";
        cmd.Parameters.Add(new SqlParameter("@used", System.Data.SqlDbType.DateTime2) { Value = usedAt });
        cmd.Parameters.Add(new SqlParameter("@id", refreshId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RevokeFamilyAsync(Guid familyId, DateTime revokedAt, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dbo.auth_refresh_tokens SET revoked_at = @revoked WHERE family_id = @family AND revoked_at IS NULL";
        cmd.Parameters.Add(new SqlParameter("@revoked", System.Data.SqlDbType.DateTime2) { Value = revokedAt });
        cmd.Parameters.Add(new SqlParameter("@family", familyId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RevokeAllForUserAsync(int userId, DateTime revokedAt, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dbo.auth_refresh_tokens SET revoked_at = @revoked WHERE user_id = @user AND revoked_at IS NULL";
        cmd.Parameters.Add(new SqlParameter("@revoked", System.Data.SqlDbType.DateTime2) { Value = revokedAt });
        cmd.Parameters.Add(new SqlParameter("@user", userId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PurgeExpiredAsync(int userId, DateTime cutoff, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE TOP (100) FROM dbo.auth_refresh_tokens WHERE user_id = @user AND expires_at < @cutoff";
        cmd.Parameters.Add(new SqlParameter("@user", userId));
        cmd.Parameters.Add(new SqlParameter("@cutoff", System.Data.SqlDbType.DateTime2) { Value = cutoff });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
