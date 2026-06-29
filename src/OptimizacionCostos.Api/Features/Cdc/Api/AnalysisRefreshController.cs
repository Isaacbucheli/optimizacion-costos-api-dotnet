using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Features.Cdc.Api;

/// <summary>
/// Refrescos a nivel de análisis que dependen de Azure (B5). Port de los endpoints
/// ri-coverage/refresh y power-history/refresh de app/routes/cost_calculation.py, que el
/// controller de costos había omitido por depender de la capa Azure (ya disponible: B1).
/// </summary>
[ApiController]
[Authorize]
public sealed class AnalysisRefreshController(
    IRiCoverageService riCoverage,
    IPowerHistoryService powerHistory,
    IAnalysisAccess access,
    ILogger<AnalysisRefreshController> logger) : ControllerBase
{
    [HttpPost("analysis/{analysisId:int}/ri-coverage/refresh")]
    public async Task<IActionResult> RefreshRiCoverage(int analysisId, CancellationToken ct)
    {
        var chk = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!chk.Ok) return Translate(chk);
        try { return Ok(await riCoverage.ComputeAsync(analysisId, ct)); }
        catch (Exception ex)
        {
            logger.LogError(ex, "RI coverage refresh failed type={Type}", ex.GetType().Name);
            return Problem(statusCode: 500, detail: $"RI coverage refresh failed: {ex.GetType().Name}");
        }
    }

    [HttpPost("analysis/{analysisId:int}/power-history/refresh")]
    public async Task<IActionResult> RefreshPowerHistory(int analysisId, CancellationToken ct)
    {
        var chk = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!chk.Ok) return Translate(chk);
        try { return Ok(await powerHistory.ComputeAsync(analysisId, ct)); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Power history refresh failed type={Type}", ex.GetType().Name);
            return Problem(statusCode: 500, detail: $"Power history refresh failed: {ex.GetType().Name}");
        }
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
