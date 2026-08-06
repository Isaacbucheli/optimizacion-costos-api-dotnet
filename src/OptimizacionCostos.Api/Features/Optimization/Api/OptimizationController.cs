using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Clients;
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
[RequireModule(Modules.Optimization)]
public sealed class OptimizationController(
    IOptimizationService svc,
    IAnalysisAccess access,
    IClientStore clients,
    IOptimizationExcelExporter excel,
    IOperationCooldown cooldown,
    AppConfig config,
    ILogger<OptimizationController> logger) : ControllerBase
{
    public sealed record StateUpdateRequest(string? State, string? Notes);
    private static readonly HashSet<string> ValidStates = new(StringComparer.Ordinal) { "abierto", "en_progreso", "resuelto", "ignorado" };
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private string? Email => User.FindFirst("sub")?.Value;

    [HttpGet("access")]
    public IActionResult CheckAccess() => Ok(new { allowed = svc.AccessAllowed(Email) });

    [HttpPost("clients/{clientId:int}/scan")]
    [RequireModule(Modules.Optimization, ModuleAccess.Edit)]
    public async Task<IActionResult> RunScan(int clientId, CancellationToken ct)
    {
        if (!svc.AccessAllowed(Email)) return Forbid403();
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        // El barrido recorre el tenant del cliente contra Resource Graph. Los recursos no cambian en
        // segundos, asi que repetirlo solo gasta llamadas contra su suscripcion. Va DESPUES del control
        // de acceso para no filtrar por el 429 que un cliente ajeno existe.
        var falta = await cooldown.TryBeginAsync(
            "optimization-scan", clientId, TimeSpan.FromMinutes(config.OperationCooldownMinutes), ct);
        if (falta is not null) return this.EnCooldown(falta.Value, "El barrido de optimización");

        try { return Ok(await svc.RunScanAsync(clientId, Email, ct)); }
        catch (NoManagedSubscriptionsException ex) { return BadRequest(new { detail = ex.Message }); }
        catch (Exception ex)
        {
            logger.LogError(ex, "scan falló client_id={Cid}", clientId);
            return Problem(statusCode: 500, detail: "El barrido no pudo completarse.");
        }
    }

    [HttpGet("clients/{clientId:int}/scans")]
    public async Task<IActionResult> ListScans(int clientId, CancellationToken ct)
    {
        if (!svc.AccessAllowed(Email)) return Forbid403();
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        return Ok(await svc.ListScansAsync(clientId, ct));
    }

    [HttpGet("scans/{scanId:int}/findings")]
    public async Task<IActionResult> ScanFindings(int scanId, CancellationToken ct)
    {
        if (!svc.AccessAllowed(Email)) return Forbid403();
        var owner = await svc.ScanOwnerAsync(scanId, ct);
        if (owner is null) return NotFound(new { detail = "Barrido no encontrado." });
        var chk = await access.AssertClientAccessAsync(User, owner.Value, ct);
        if (!chk.Ok) return Translate(chk);
        return Ok(await svc.ScanFindingsAsync(scanId, ct));
    }

    [HttpGet("scans/{scanId:int}/excel")]
    public async Task<IActionResult> ExportExcel(int scanId, [FromQuery] string? states, CancellationToken ct)
    {
        if (!svc.AccessAllowed(Email)) return Forbid403();

        var requested = (states ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested.Any(s => !ValidStates.Contains(s)))
            return BadRequest(new { detail = "states inválido" });

        var owner = await svc.ScanOwnerAsync(scanId, ct);
        if (owner is null) return NotFound(new { detail = "Barrido no encontrado." });
        var chk = await access.AssertClientAccessAsync(User, owner.Value, ct);
        if (!chk.Ok) return Translate(chk);

        var scans = await svc.ListScansAsync(owner.Value, ct);
        var scanRow = scans.FirstOrDefault(s =>
            s.TryGetValue("scan_id", out var id) && Convert.ToInt32(id) == scanId);
        var scanDate = scanRow is not null
            && DateTime.TryParse(scanRow.GetValueOrDefault("started_at") as string, out var d) ? d : DateTime.Now;
        var subsScanned = scanRow is not null ? Convert.ToInt32(scanRow.GetValueOrDefault("subscriptions_scanned") ?? 0) : 0;

        var findings = OptimizationExcelLabels.FilterByStates(await svc.ScanFindingsAsync(scanId, ct), requested);
        var clientName = await clients.GetNameAsync(owner.Value, ct) ?? $"cliente-{owner.Value}";

        var result = excel.Generate(new OptExcelScanInfo(clientName, scanDate, subsScanned), findings, requested);
        return File(result.Bytes, XlsxContentType, result.FileName);
    }

    [HttpPut("findings/{fingerprintHex}/state")]
    [RequireModule(Modules.Optimization, ModuleAccess.Edit)]
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
