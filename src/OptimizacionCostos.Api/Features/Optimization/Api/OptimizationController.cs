using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Features.Optimization.Api;

/// <summary>
/// Optimización Azure (barrido de tenant). Port de app/routes/optimization.py (prefix /optimization).
/// Gating doble: rol admin/consultor + lista blanca de emails (OPTIMIZATION_ALLOWED_EMAILS).
/// El export Excel se añade con el bloque transversal de Excel (B6/B7/B8).
/// </summary>
[ApiController]
[Authorize]
[Route("optimization")]
public sealed class OptimizationController(
    IOptimizationService svc,
    IAnalysisAccess access,
    ILogger<OptimizationController> logger) : ControllerBase
{
    public sealed record StateUpdateRequest(string? State, string? Notes);
    private static readonly HashSet<string> ValidStates = new(StringComparer.Ordinal) { "abierto", "en_progreso", "resuelto", "ignorado" };

    private string? Email => User.FindFirst("sub")?.Value;

    [HttpGet("access")]
    public IActionResult CheckAccess() => Ok(new { allowed = svc.AccessAllowed(Email) });

    [HttpPost("clients/{clientId:int}/scan")]
    [Authorize(Roles = Roles.Editors)]
    public async Task<IActionResult> RunScan(int clientId, CancellationToken ct)
    {
        if (!svc.AccessAllowed(Email)) return Forbid403();
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        try { return Ok(await svc.RunScanAsync(clientId, Email, ct)); }
        catch (NoManagedSubscriptionsException ex) { return BadRequest(new { detail = ex.Message }); }
        catch (Exception ex)
        {
            logger.LogError(ex, "scan falló client_id={Cid}", clientId);
            return Problem(statusCode: 500, detail: "El barrido no pudo completarse.");
        }
    }

    [HttpGet("clients/{clientId:int}/scans")]
    [Authorize(Roles = Roles.Editors)]
    public async Task<IActionResult> ListScans(int clientId, CancellationToken ct)
    {
        if (!svc.AccessAllowed(Email)) return Forbid403();
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        return Ok(await svc.ListScansAsync(clientId, ct));
    }

    [HttpGet("scans/{scanId:int}/findings")]
    [Authorize(Roles = Roles.Editors)]
    public async Task<IActionResult> ScanFindings(int scanId, CancellationToken ct)
    {
        if (!svc.AccessAllowed(Email)) return Forbid403();
        var owner = await svc.ScanOwnerAsync(scanId, ct);
        if (owner is null) return NotFound(new { detail = "Barrido no encontrado." });
        var chk = await access.AssertClientAccessAsync(User, owner.Value, ct);
        if (!chk.Ok) return Translate(chk);
        return Ok(await svc.ScanFindingsAsync(scanId, ct));
    }

    [HttpPut("findings/{fingerprintHex}/state")]
    [Authorize(Roles = Roles.Editors)]
    public async Task<IActionResult> UpdateState(string fingerprintHex, [FromBody] StateUpdateRequest payload, CancellationToken ct)
    {
        if (!svc.AccessAllowed(Email)) return Forbid403();
        if (payload.State is null || !ValidStates.Contains(payload.State))
            return BadRequest(new { detail = "state inválido" });
        byte[] fingerprint;
        try { fingerprint = Convert.FromHexString(fingerprintHex); }
        catch (FormatException) { return BadRequest(new { detail = "Fingerprint inválido." }); }

        var owner = await svc.FindingStateOwnerAsync(fingerprint, ct);
        if (owner is null) return NotFound(new { detail = "Hallazgo no encontrado." });
        var chk = await access.AssertClientAccessAsync(User, owner.Value, ct);
        if (!chk.Ok) return Translate(chk);

        await svc.UpdateStateAsync(fingerprint, payload.State, payload.Notes, Email, ct);
        return Ok(new { fingerprint = fingerprintHex, state = payload.State });
    }

    private IActionResult Forbid403() => StatusCode(StatusCodes.Status403Forbidden, new { detail = "Modulo no disponible para este usuario." });

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
