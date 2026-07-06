using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Configuration;

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
    ILighthouseCatalogService catalog) : ControllerBase
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
