using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Features.AzureIntegration;

/// <summary>
/// Servicio de Key Vault. Port 1:1 de app/keyvault.py.
///
/// Reglas (idénticas al Python):
///   - El client_secret SOLO vive aquí (en Key Vault).
///   - Nunca se loguea, nunca se devuelve en endpoints, nunca se serializa.
///   - <see cref="ReadSecretAsync"/> SOLO debe usarse dentro de
///     <see cref="SqlAzureCredentialFactory"/> para construir credenciales; el llamador
///     NO debe propagar el string a otras capas.
/// </summary>
public interface IKeyVaultService
{
    /// <summary>Nombre único de secreto: <c>cred-{clientId}-{16 hex}</c> (paridad con Python).</summary>
    string GenerateSecretName(int clientId);

    /// <summary>Guarda (o crea nueva versión de) un secreto. Nunca loguea el valor.</summary>
    Task StoreSecretAsync(string secretName, string secretValue, CancellationToken ct = default);

    /// <summary>Lee un secreto. SOLO para construir credenciales; no propagar el valor.</summary>
    Task<string> ReadSecretAsync(string secretName, CancellationToken ct = default);

    /// <summary>Soft-delete del secreto (compensación si falla la inserción en SQL).</summary>
    Task DeleteSecretAsync(string secretName, CancellationToken ct = default);
}

/// <summary>
/// Implementación con Azure.Security.KeyVault.Secrets + DefaultAzureCredential
/// (la identidad del App Service). Equivale a <c>_get_secret_client()</c> del Python.
/// </summary>
public sealed class KeyVaultService(AppConfig config, ILogger<KeyVaultService> logger) : IKeyVaultService
{
    // Cliente perezoso: se construye una vez (DefaultAzureCredential resuelve la identidad
    // gestionada en Azure / az login en local).
    private readonly Lazy<SecretClient> _client = new(() =>
        new SecretClient(new Uri(config.KeyVaultUrl), new DefaultAzureCredential()));

    public string GenerateSecretName(int clientId)
    {
        // Python: f"cred-{client_id}-{uuid4().hex[:16]}". Solo letras/números/guiones, máx 127.
        var shortId = Guid.NewGuid().ToString("N")[..16];
        return $"cred-{clientId}-{shortId}";
    }

    public async Task StoreSecretAsync(string secretName, string secretValue, CancellationToken ct = default)
    {
        await _client.Value.SetSecretAsync(secretName, secretValue, ct);
        logger.LogInformation("Secret stored in Key Vault (name only): {SecretName}", secretName);
    }

    public async Task<string> ReadSecretAsync(string secretName, CancellationToken ct = default)
    {
        try
        {
            KeyVaultSecret kv = await _client.Value.GetSecretAsync(secretName, cancellationToken: ct);
            return kv.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new InvalidOperationException($"Secret not found in Key Vault: {secretName}");
        }
    }

    public async Task DeleteSecretAsync(string secretName, CancellationToken ct = default)
    {
        try
        {
            var op = await _client.Value.StartDeleteSecretAsync(secretName, ct);
            await op.WaitForCompletionAsync(ct);
            logger.LogInformation("Secret deleted from Key Vault: {SecretName}", secretName);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            logger.LogWarning("Secret not found while deleting: {SecretName}", secretName);
        }
    }
}
