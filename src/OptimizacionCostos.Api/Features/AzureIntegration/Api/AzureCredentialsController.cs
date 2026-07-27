using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Features.AzureIntegration.Api;

/// <summary>
/// Gestión de credenciales Azure por cliente. Port de app/routes/azure_credentials.py
/// (prefix /azure/credentials). Seguridad: el client_secret NUNCA se devuelve ni se loguea;
/// vive solo en Key Vault. Toda alta/rotación valida contra Azure antes de confirmar estado.
/// </summary>
[ApiController]
[Authorize]
[Route("azure/credentials")]
public sealed class AzureCredentialsController(
    IClientCredentialStore store,
    IKeyVaultService keyVault,
    IAzureCredentialFactory factory,
    IAnalysisAccess access,
    ILogger<AzureCredentialsController> logger) : ControllerBase
{
    public sealed record CredentialCreateRequest(
        int ClientId, string? CredentialName, string? TenantId, string? AppClientId, string? ClientSecret, DateTime? SecretExpiresAt);
    public sealed record RotateSecretRequest(string? ClientSecret, DateTime? SecretExpiresAt);
    public sealed record CredentialUpdateRequest(string? CredentialName, bool? IsActive);

    // -------------------- GET /azure/credentials?client_id= --------------------
    [HttpGet]
    public async Task<IActionResult> List([FromQuery(Name = "client_id")] int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        return Ok(await store.ListByClientAsync(clientId, ct));
    }

    // -------------------- POST /azure/credentials --------------------
    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Create([FromBody] CredentialCreateRequest payload, CancellationToken ct)
    {
        // Control de acceso por cliente (paridad con List/Audit): el usuario debe poder operar
        // sobre el cliente indicado antes de tocar Key Vault o SQL.
        var accessChk = await access.AssertClientAccessAsync(User, payload.ClientId, ct);
        if (!accessChk.Ok) return Translate(accessChk);

        if (string.IsNullOrWhiteSpace(payload.CredentialName) || string.IsNullOrWhiteSpace(payload.TenantId)
            || string.IsNullOrWhiteSpace(payload.AppClientId) || string.IsNullOrEmpty(payload.ClientSecret))
            return BadRequest(new { detail = "credential_name, tenant_id, app_client_id y client_secret son requeridos" });

        var secretName = keyVault.GenerateSecretName(payload.ClientId);
        var storedInKv = false;
        try
        {
            // 1) Guardar el secreto en Key Vault primero.
            await keyVault.StoreSecretAsync(secretName, payload.ClientSecret!, ct);
            storedInKv = true;

            // 2) Insertar metadata en SQL.
            var newId = await store.InsertAsync(
                payload.ClientId, payload.CredentialName!, payload.TenantId!, payload.AppClientId!, secretName, payload.SecretExpiresAt, ct);

            // 3) Auditar + validar contra Azure.
            await factory.WriteAuditLogAsync(newId, "created", details: $"Credential '{payload.CredentialName}' created", ct: ct);
            var result = await factory.TestCredentialAuthAsync(newId, ct);
            await factory.UpdateValidationStatusAsync(newId, result.Success, result.Error, ct);

            return Ok(new { message = "Credencial creada correctamente", credential_id = newId, validation = result });
        }
        catch (Exception ex)
        {
            // Compensación: si el secreto quedó en KV pero falló SQL, limpiarlo.
            if (storedInKv)
            {
                try { await keyVault.DeleteSecretAsync(secretName, ct); }
                catch (Exception cleanupEx) { logger.LogError(cleanupEx, "Failed to rollback secret in Key Vault: {Name}", secretName); }
            }
            logger.LogError(ex, "Error creating credential type={Type}", ex.GetType().Name);
            return Problem(statusCode: 500, detail: $"Error creando credencial: {ex.GetType().Name}");
        }
    }

    // -------------------- PUT /azure/credentials/{id}/secret --------------------
    [HttpPut("{credentialId:int}/secret")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> RotateSecret(int credentialId, [FromBody] RotateSecretRequest payload, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(payload.ClientSecret))
            return BadRequest(new { detail = "client_secret es requerido" });

        var accessError = await AssertCredentialAccessAsync(credentialId, ct);
        if (accessError is not null) return accessError;

        var secretName = await store.GetSecretNameAsync(credentialId, ct);
        if (secretName is null) return NotFound(new { detail = "Credencial no encontrada" });

        await keyVault.StoreSecretAsync(secretName, payload.ClientSecret!, ct);
        await store.UpdateRotateMetaAsync(credentialId, payload.SecretExpiresAt, ct);
        // El factory memoiza la credencial por scope: sin esto, la validación de más abajo podría
        // usar el secreto anterior si algo ya la había construido en esta misma request.
        factory.InvalidateCachedCredential(credentialId);

        await factory.WriteAuditLogAsync(credentialId, "secret_rotated", details: "Secret rotated", ct: ct);
        var result = await factory.TestCredentialAuthAsync(credentialId, ct);
        await factory.UpdateValidationStatusAsync(credentialId, result.Success, result.Error, ct);
        return Ok(new { message = "Secreto rotado correctamente", validation = result });
    }

    // -------------------- PUT /azure/credentials/{id} --------------------
    [HttpPut("{credentialId:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Update(int credentialId, [FromBody] CredentialUpdateRequest payload, CancellationToken ct)
    {
        if (payload.CredentialName is null && payload.IsActive is null)
            return BadRequest(new { detail = "Nada que actualizar" });

        var accessError = await AssertCredentialAccessAsync(credentialId, ct);
        if (accessError is not null) return accessError;

        var ok = await store.UpdateMetaAsync(credentialId, payload.CredentialName, payload.IsActive, ct);
        if (!ok) return NotFound(new { detail = "Credencial no encontrada" });

        var action = payload.IsActive is false ? "deactivated" : "updated";
        await factory.WriteAuditLogAsync(credentialId, action, details: "Updated fields", ct: ct);
        return Ok(new { message = "Credencial actualizada correctamente" });
    }

    // -------------------- POST /azure/credentials/{id}/test --------------------
    [HttpPost("{credentialId:int}/test")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Test(int credentialId, CancellationToken ct)
    {
        var accessError = await AssertCredentialAccessAsync(credentialId, ct);
        if (accessError is not null) return accessError;

        var result = await factory.TestCredentialAuthAsync(credentialId, ct);
        await factory.UpdateValidationStatusAsync(credentialId, result.Success, result.Error, ct);
        await factory.WriteAuditLogAsync(credentialId, "validated", details: $"success={result.Success}", ct: ct);
        return Ok(result);
    }

    // -------------------- GET /azure/credentials/{id}/audit?limit= --------------------
    [HttpGet("{credentialId:int}/audit")]
    public async Task<IActionResult> Audit(int credentialId, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var clientId = await store.ClientIdForAsync(credentialId, ct);
        if (clientId is null) return NotFound(new { detail = "Credential not found" });
        var chk = await access.AssertClientAccessAsync(User, clientId.Value, ct);
        if (!chk.Ok) return Translate(chk);
        return Ok(await store.GetAuditAsync(credentialId, limit, ct));
    }

    /// <summary>
    /// Resuelve el cliente dueño de la credencial y verifica el acceso del usuario (mismo patrón
    /// que Audit). Devuelve null si puede operar; 404 si la credencial no existe; 403 si el cliente
    /// no es accesible. Evita el IDOR de operar sobre credenciales de otros clientes por id.
    /// </summary>
    private async Task<IActionResult?> AssertCredentialAccessAsync(int credentialId, CancellationToken ct)
    {
        var clientId = await store.ClientIdForAsync(credentialId, ct);
        if (clientId is null) return NotFound(new { detail = "Credencial no encontrada" });
        var chk = await access.AssertClientAccessAsync(User, clientId.Value, ct);
        return chk.Ok ? null : Translate(chk);
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
