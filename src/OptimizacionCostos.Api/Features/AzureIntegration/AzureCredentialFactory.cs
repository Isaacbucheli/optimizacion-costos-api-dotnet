using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.AzureIntegration;

/// <summary>La credencial no existe en dbo.client_azure_credentials.</summary>
public sealed class CredentialNotFoundException(int credentialId)
    : Exception($"Credential not found: {credentialId}");

/// <summary>La credencial existe pero está inactiva (is_active = 0).</summary>
public sealed class CredentialInactiveException(int credentialId)
    : Exception($"Credential is inactive: {credentialId}");

/// <summary>Resultado de probar la autenticación de una credencial (sin exponer el secreto).</summary>
public sealed record CredentialAuthResult(bool Success, long? ExpiresOn, string? Error);

/// <summary>
/// Factoría que entrega credenciales Azure listas para usar contra ARM / Resource Graph.
/// Port 1:1 de app/azure_client_factory.py.
///
/// Garantía de seguridad (idéntica al Python): el client_secret NO sale de este módulo como
/// string — se lee de Key Vault y se inyecta directo en <see cref="ClientSecretCredential"/>;
/// el llamador recibe un <see cref="TokenCredential"/>, nunca el secreto.
/// </summary>
public interface IAzureCredentialFactory
{
    /// <summary>Construye un ClientSecretCredential listo para usar. El secreto no se retorna.</summary>
    Task<TokenCredential> GetClientSecretCredentialAsync(int credentialId, CancellationToken ct = default);

    /// <summary>Valida que la credencial obtenga un token contra ARM. No expone el secreto.</summary>
    Task<CredentialAuthResult> TestCredentialAuthAsync(int credentialId, CancellationToken ct = default);

    /// <summary>Actualiza last_validated_at / last_validation_status / error en SQL.</summary>
    Task UpdateValidationStatusAsync(int credentialId, bool success, string? errorMessage = null, CancellationToken ct = default);

    /// <summary>Registra una entrada de auditoría. No recibe ni guarda secretos.</summary>
    Task WriteAuditLogAsync(int credentialId, string action, string? actor = null, string? details = null, CancellationToken ct = default);
}

public sealed class SqlAzureCredentialFactory(
    ISqlConnectionFactory factory,
    IKeyVaultService keyVault,
    ILogger<SqlAzureCredentialFactory> logger) : IAzureCredentialFactory
{
    // Scope estándar para ARM. No requiere permisos extra.
    private const string ArmScope = "https://management.azure.com/.default";

    private sealed record CredentialRow(
        int CredentialId, int ClientId, string? CredentialName,
        string TenantId, string AppClientId, string KeyVaultSecretName,
        string AuthType, string? SessionUserEmail);

    private async Task<CredentialRow> FetchCredentialRowAsync(int credentialId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await AzureCredentialSchema.EnsureAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT credential_id, client_id, credential_name, tenant_id,
                   app_client_id, key_vault_secret_name, is_active,
                   COALESCE(auth_type, 'app_secret'), session_user_email
            FROM dbo.client_azure_credentials
            WHERE credential_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", credentialId));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new CredentialNotFoundException(credentialId);

        var isActive = !reader.IsDBNull(6) && reader.GetBoolean(6);
        if (!isActive)
            throw new CredentialInactiveException(credentialId);

        return new CredentialRow(
            CredentialId: reader.GetInt32(0),
            ClientId: reader.GetInt32(1),
            CredentialName: reader.IsDBNull(2) ? null : reader.GetString(2),
            TenantId: reader.GetString(3),
            AppClientId: reader.GetString(4),
            KeyVaultSecretName: reader.IsDBNull(5) ? "" : reader.GetString(5),
            AuthType: reader.GetString(7),
            SessionUserEmail: reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    public async Task<TokenCredential> GetClientSecretCredentialAsync(int credentialId, CancellationToken ct = default)
    {
        var row = await FetchCredentialRowAsync(credentialId, ct);

        // El secreto se lee y se inyecta directo; la variable local sale de scope al retornar.
        var secretValue = await keyVault.ReadSecretAsync(row.KeyVaultSecretName, ct);
        return new ClientSecretCredential(row.TenantId, row.AppClientId, secretValue);
    }

    public async Task<CredentialAuthResult> TestCredentialAuthAsync(int credentialId, CancellationToken ct = default)
    {
        try
        {
            var credential = await GetClientSecretCredentialAsync(credentialId, ct);
            var token = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
            return new CredentialAuthResult(true, token.ExpiresOn.ToUnixTimeSeconds(), null);
        }
        catch (CredentialNotFoundException ex)
        {
            return new CredentialAuthResult(false, null, ex.Message);
        }
        catch (CredentialInactiveException ex)
        {
            return new CredentialAuthResult(false, null, ex.Message);
        }
        catch (Exception ex)
        {
            // No exponemos detalles internos. Logueamos el tipo, nunca el secreto.
            logger.LogError(ex, "Auth test failed for credential_id={CredentialId} type={Type}",
                credentialId, ex.GetType().Name);
            var msg = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            return new CredentialAuthResult(false, null, $"{ex.GetType().Name}: {msg}");
        }
    }

    public async Task UpdateValidationStatusAsync(
        int credentialId, bool success, string? errorMessage = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.client_azure_credentials
            SET last_validated_at = SYSUTCDATETIME(),
                last_validation_status = @status,
                last_validation_error = @error
            WHERE credential_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@status", success ? "success" : "failed"));
        cmd.Parameters.Add(new SqlParameter("@error",
            (object?)(errorMessage is { Length: > 1000 } ? errorMessage[..1000] : errorMessage) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@id", credentialId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task WriteAuditLogAsync(
        int credentialId, string action, string? actor = null, string? details = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.credential_audit_log (credential_id, action, actor, details)
            VALUES (@id, @action, @actor, @details)
            """;
        cmd.Parameters.Add(new SqlParameter("@id", credentialId));
        cmd.Parameters.Add(new SqlParameter("@action", action));
        cmd.Parameters.Add(new SqlParameter("@actor", (object?)actor ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@details", (object?)details ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
