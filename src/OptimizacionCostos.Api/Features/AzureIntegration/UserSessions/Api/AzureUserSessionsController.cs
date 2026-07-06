using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Features.AzureIntegration.UserSessions.Api;

/// <summary>
/// Sesiones Azure con cuenta de usuario (Lighthouse). Gateado por USER_SESSION_AUTH_ENABLED
/// (404 apagado) y por lista de emails permitidos (vacía = solo admins). El token de la
/// sesión NUNCA sale en las respuestas.
/// </summary>
[ApiController]
[Authorize]
[Route("azure/user-sessions")]
public sealed class AzureUserSessionsController(
    IAzureUserSessionService sessions,
    AppConfig config,
    ILighthouseCatalogService catalog,
    IClientStore clients,
    IClientCredentialStore credStore,
    IClientSubscriptionStore subStore,
    IAzureCredentialFactory credFactory,
    IAnalysisAccess access) : ControllerBase
{
    private string? Email => User.FindFirst("sub")?.Value;

    // -------------------- POST /azure/user-sessions --------------------
    [HttpPost]
    public IActionResult Start()
    {
        if (Gate() is { } blocked) return blocked;
        var snap = sessions.Start(Email!);
        return Accepted(snap);
    }

    // -------------------- GET /azure/user-sessions/current --------------------
    [HttpGet("current")]
    public IActionResult Status()
    {
        if (Gate() is { } blocked) return blocked;
        var snap = sessions.GetStatus(Email!);
        return snap is null ? Ok(new { status = "none" }) : Ok(snap);
    }

    // -------------------- DELETE /azure/user-sessions/current --------------------
    [HttpDelete("current")]
    public IActionResult Disconnect()
    {
        if (Gate() is { } blocked) return blocked;
        sessions.Disconnect(Email!);
        return Ok(new { message = "Sesión desconectada" });
    }

    // -------------------- GET /azure/user-sessions/current/clients --------------------
    [HttpGet("current/clients")]
    public async Task<IActionResult> Clients([FromQuery] bool refresh = false, CancellationToken ct = default)
    {
        if (Gate() is { } blocked) return blocked;
        try
        {
            return Ok(await catalog.GetClientsAsync(Email!, refresh, ct));
        }
        catch (UserSessionExpiredException ex)
        {
            return Conflict(new { detail = ex.Message }); // 409 → el front pide reconectar
        }
    }

    public sealed record LinkSubscriptionDto(string? SubscriptionId, string? DisplayName);
    public sealed record LinkRequest(int? ClientId, string? NewClientName, string? TenantId, List<LinkSubscriptionDto>? Subscriptions);

    // -------------------- POST /azure/user-sessions/current/link --------------------
    [HttpPost("current/link")]
    public async Task<IActionResult> Link([FromBody] LinkRequest payload, CancellationToken ct)
    {
        if (Gate() is { } blocked) return blocked;
        var email = Email!;

        if (sessions.GetStatus(email)?.Status != "authenticated")
            return Conflict(new { detail = "No hay sesión Azure autenticada; conecta tu cuenta primero" });

        var subs = (payload.Subscriptions ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.SubscriptionId)).ToList();
        if (string.IsNullOrWhiteSpace(payload.TenantId) || subs.Count == 0)
            return BadRequest(new { detail = "tenant_id y subscriptions son requeridos" });
        if ((payload.ClientId is null) == string.IsNullOrWhiteSpace(payload.NewClientName))
            return BadRequest(new { detail = "Indica client_id O new_client_name (exactamente uno)" });

        int clientId;
        if (payload.ClientId is int existing)
        {
            var chk = await access.AssertClientAccessAsync(User, existing, ct);
            if (!chk.Ok) return Translate(chk);
            clientId = existing;
        }
        else
        {
            var name = payload.NewClientName!.Trim();
            if (await clients.NameExistsAsync(name, excludeClientId: 0, ct))
                return BadRequest(new { detail = $"Ya existe un cliente llamado '{name}'" });
            clientId = await clients.CreateAsync(name, null, null, null, "Cliente temporal (Lighthouse)", ct);
        }

        var credId = await credStore.InsertUserSessionAsync(
            clientId, $"Sesión de {email}", payload.TenantId!.Trim(), config.UserSessionClientId, email, ct);

        var linked = 0;
        foreach (var s in subs)
        {
            await subStore.UpsertAsync(clientId, credId, s.SubscriptionId!.Trim(), s.DisplayName, ct);
            linked++;
        }

        await credFactory.WriteAuditLogAsync(credId, "created", actor: email,
            details: $"user_session link: {linked} suscripciones, tenant {payload.TenantId}", ct: ct);

        return Ok(new { client_id = clientId, credential_id = credId, subscriptions_linked = linked });
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };

    /// <summary>404 si el feature está apagado; 403 si el usuario no es admin ni está en la lista.</summary>
    private IActionResult? Gate()
    {
        if (!config.UserSessionAuthEnabled) return NotFound();
        var email = Email;
        if (string.IsNullOrEmpty(email)) return Forbid();
        if (User.IsInRole(Roles.Admin)) return null;
        var allowed = config.UserSessionAllowedEmails
            .Any(e => string.Equals(e, email.Trim(), StringComparison.OrdinalIgnoreCase));
        return allowed ? null
            : StatusCode(StatusCodes.Status403Forbidden, new { detail = "No autorizado para sesiones Azure de usuario" });
    }
}
