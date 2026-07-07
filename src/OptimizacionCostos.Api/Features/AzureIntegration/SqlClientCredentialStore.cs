using System.Globalization;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.AzureIntegration;

/// <summary>Metadata pública de una credencial (NUNCA incluye el client_secret).</summary>
public sealed record CredentialListItem(
    int CredentialId, int ClientId, string? CredentialName, string TenantId, string AppClientId,
    string? SecretExpiresAt, string? LastValidatedAt, string? LastValidationStatus,
    bool IsActive, string? CreatedAt, string? UpdatedAt);

public sealed record CredentialAuditItem(int AuditId, string? Action, string? Actor, string? Details, string? OccurredAt);

/// <summary>
/// Acceso a dbo.client_azure_credentials + dbo.credential_audit_log. Port de los accesos SQL de
/// app/routes/azure_credentials.py. El secreto vive solo en Key Vault (ver B1); aquí solo metadata.
/// </summary>
public interface IClientCredentialStore
{
    Task<IReadOnlyList<CredentialListItem>> ListByClientAsync(int clientId, CancellationToken ct = default);
    Task<int> InsertAsync(int clientId, string name, string tenantId, string appClientId, string secretName, DateTime? secretExpiresAt, CancellationToken ct = default);
    Task<string?> GetSecretNameAsync(int credentialId, CancellationToken ct = default);
    Task UpdateRotateMetaAsync(int credentialId, DateTime? secretExpiresAt, CancellationToken ct = default);
    Task<bool> UpdateMetaAsync(int credentialId, string? name, bool? isActive, CancellationToken ct = default);
    Task<int?> ClientIdForAsync(int credentialId, CancellationToken ct = default);
    Task<IReadOnlyList<CredentialAuditItem>> GetAuditAsync(int credentialId, int limit, CancellationToken ct = default);

    /// <summary>Credencial efímera de sesión de usuario (Lighthouse). Sin secreto en Key Vault.
    /// IDEMPOTENTE: si ya existe una credencial user_session para (cliente, email, tenant), la
    /// reutiliza (reactiva + refresca metadata) en vez de crear otra — re-sincronizar el mismo
    /// cliente NO acumula credenciales.</summary>
    Task<int> UpsertUserSessionAsync(int clientId, string name, string tenantId, string appClientId, string sessionUserEmail, CancellationToken ct = default);
}

public sealed class SqlClientCredentialStore(ISqlConnectionFactory factory) : IClientCredentialStore
{
    // Mimetiza str(datetime) de Python: "yyyy-MM-dd HH:mm:ss.ffffff".
    private static string? D(SqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetValue(i) is DateTime dt
            ? dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
            : r.GetValue(i).ToString();

    public async Task<IReadOnlyList<CredentialListItem>> ListByClientAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT credential_id, client_id, credential_name, tenant_id, app_client_id,
                   secret_expires_at, last_validated_at, last_validation_status,
                   is_active, created_at, updated_at
            FROM dbo.client_azure_credentials
            WHERE client_id = @cid ORDER BY created_at DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var list = new List<CredentialListItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new CredentialListItem(
                r.GetInt32(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.GetString(3), r.GetString(4), D(r, 5), D(r, 6),
                r.IsDBNull(7) ? null : r.GetString(7), !r.IsDBNull(8) && r.GetBoolean(8), D(r, 9), D(r, 10)));
        return list;
    }

    public async Task<int> InsertAsync(
        int clientId, string name, string tenantId, string appClientId, string secretName, DateTime? secretExpiresAt, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.client_azure_credentials
                (client_id, credential_name, tenant_id, app_client_id, key_vault_secret_name, secret_expires_at)
            OUTPUT INSERTED.credential_id
            VALUES (@cid, @name, @tenant, @app, @secretName, @exp)
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@name", name));
        cmd.Parameters.Add(new SqlParameter("@tenant", tenantId));
        cmd.Parameters.Add(new SqlParameter("@app", appClientId));
        cmd.Parameters.Add(new SqlParameter("@secretName", secretName));
        cmd.Parameters.Add(new SqlParameter("@exp", (object?)secretExpiresAt ?? DBNull.Value));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<string?> GetSecretNameAsync(int credentialId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key_vault_secret_name FROM dbo.client_azure_credentials WHERE credential_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", credentialId));
        return (await cmd.ExecuteScalarAsync(ct)) as string;
    }

    public async Task UpdateRotateMetaAsync(int credentialId, DateTime? secretExpiresAt, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.client_azure_credentials
            SET secret_expires_at = @exp, updated_at = SYSUTCDATETIME()
            WHERE credential_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@exp", (object?)secretExpiresAt ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@id", credentialId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> UpdateMetaAsync(int credentialId, string? name, bool? isActive, CancellationToken ct = default)
    {
        var sets = new List<string>();
        var ps = new List<SqlParameter>();
        if (name is not null) { sets.Add("credential_name = @name"); ps.Add(new SqlParameter("@name", name)); }
        if (isActive is not null) { sets.Add("is_active = @active"); ps.Add(new SqlParameter("@active", isActive.Value ? 1 : 0)); }
        sets.Add("updated_at = SYSUTCDATETIME()");

        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE dbo.client_azure_credentials SET {string.Join(", ", sets)} WHERE credential_id = @id";
        foreach (var p in ps) cmd.Parameters.Add(p);
        cmd.Parameters.Add(new SqlParameter("@id", credentialId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<int?> ClientIdForAsync(int credentialId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_id FROM dbo.client_azure_credentials WHERE credential_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", credentialId));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToInt32(v);
    }

    public async Task<IReadOnlyList<CredentialAuditItem>> GetAuditAsync(int credentialId, int limit, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (@limit) audit_id, credential_id, action, actor, details, occurred_at
            FROM dbo.credential_audit_log
            WHERE credential_id = @id ORDER BY occurred_at DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@limit", limit));
        cmd.Parameters.Add(new SqlParameter("@id", credentialId));
        var list = new List<CredentialAuditItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new CredentialAuditItem(
                r.GetInt32(0), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), D(r, 5)));
        return list;
    }

    public async Task<int> UpsertUserSessionAsync(
        int clientId, string name, string tenantId, string appClientId, string sessionUserEmail, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await AzureCredentialSchema.EnsureAsync(conn, ct);

        // Idempotente: si ya hay una credencial user_session para (cliente, email, tenant) la
        // reutilizamos (reactivar + refrescar metadata) en vez de insertar otra. Así re-sincronizar
        // el mismo cliente no acumula credenciales ni choca con una constraint única de la tabla base.
        await using (var find = conn.CreateCommand())
        {
            find.CommandText = """
                SELECT TOP 1 credential_id FROM dbo.client_azure_credentials
                WHERE client_id = @cid AND auth_type = 'user_session'
                  AND session_user_email = @mail AND tenant_id = @tenant
                ORDER BY credential_id
                """;
            find.Parameters.Add(new SqlParameter("@cid", clientId));
            find.Parameters.Add(new SqlParameter("@mail", sessionUserEmail));
            find.Parameters.Add(new SqlParameter("@tenant", tenantId));
            var found = await find.ExecuteScalarAsync(ct);
            if (found is not null && found is not DBNull)
            {
                var existingId = Convert.ToInt32(found);
                await using var upd = conn.CreateCommand();
                upd.CommandText = """
                    UPDATE dbo.client_azure_credentials
                    SET credential_name = @name, app_client_id = @app, is_active = 1, updated_at = SYSUTCDATETIME()
                    WHERE credential_id = @id
                    """;
                upd.Parameters.Add(new SqlParameter("@name", name));
                upd.Parameters.Add(new SqlParameter("@app", appClientId));
                upd.Parameters.Add(new SqlParameter("@id", existingId));
                await upd.ExecuteNonQueryAsync(ct);
                return existingId;
            }
        }

        await using var cmd = conn.CreateCommand();
        // key_vault_secret_name: la factory NUNCA lo lee para user_session (usa el token de sesión en
        // memoria), PERO la tabla tiene índice único UQ_credentials_kv_secret_name. Poner '' choca con
        // la 2ª sesión (cualquier cliente) → error de constraint. Usamos un sentinela ÚNICO y
        // determinista por (cliente, tenant, email) — coincide con la clave de idempotencia de arriba,
        // así que jamás se re-inserta el mismo y no hay colisión.
        var kvSentinel = $"user-session:{clientId}:{tenantId}:{sessionUserEmail}";
        cmd.CommandText = """
            INSERT INTO dbo.client_azure_credentials
                (client_id, credential_name, tenant_id, app_client_id, key_vault_secret_name, auth_type, session_user_email)
            OUTPUT INSERTED.credential_id
            VALUES (@cid, @name, @tenant, @app, @kv, 'user_session', @mail)
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@name", name));
        cmd.Parameters.Add(new SqlParameter("@tenant", tenantId));
        cmd.Parameters.Add(new SqlParameter("@app", appClientId));
        cmd.Parameters.Add(new SqlParameter("@kv", kvSentinel));
        cmd.Parameters.Add(new SqlParameter("@mail", sessionUserEmail));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }
}
