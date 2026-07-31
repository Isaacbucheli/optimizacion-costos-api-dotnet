using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Features.Boletin.Api;

/// <summary>Boletín Azure: retiros/deprecaciones de Microsoft con impacto por cliente.
/// Sin costos (regla del proyecto para entregables de cliente).</summary>
[ApiController]
[Authorize]
[Route("boletin")]
[RequireModule(Modules.Boletin)]
public sealed class BoletinController(
    IBoletinService svc, IAnalysisAccess access, ILogger<BoletinController> logger) : ControllerBase
{
    [HttpGet("clients/{clientId:int}")]
    public async Task<IActionResult> Get(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        return Ok(await svc.GetAsync(clientId, ct));
    }

    [HttpPost("clients/{clientId:int}/sync")]
    [RequireModule(Modules.Boletin, ModuleAccess.Edit)]
    public async Task<IActionResult> Sync(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        try { return Ok(await svc.RunSyncAsync(clientId, User.FindFirst("sub")?.Value, ct)); }
        catch (BoletinNoManagedSubscriptionsException ex) { return BadRequest(new { detail = ex.Message }); }
        catch (Exception ex)
        {
            logger.LogError(ex, "boletin sync falló client_id={Cid}", clientId);
            return Problem(statusCode: 500, detail: "La sincronización no pudo completarse.");
        }
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden,
            new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
