using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Features.Cdc.Api;

/// <summary>
/// Refrescos a nivel de análisis que dependen de Azure (B5). power-history/refresh corre como job en
/// background (202 + polling): el POST encola y responde 202; GET status expone el resultado. ri-coverage
/// sigue síncrono (fuera de alcance de la conversión a job).
/// </summary>
[ApiController]
[Authorize]
public sealed class AnalysisRefreshController(
    IRiCoverageService riCoverage,
    IPowerHistoryJobQueue powerQueue,
    IPowerHistoryJobStore powerStore,
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

    /// <summary>Encola un refresh de encendido/apagado (202). Guard: no encola doble si ya corre.</summary>
    [HttpPost("analysis/{analysisId:int}/power-history/refresh")]
    public async Task<IActionResult> RefreshPowerHistory(int analysisId, CancellationToken ct)
    {
        var chk = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!chk.Ok) return Translate(chk);

        if (await powerStore.IsRunningAsync(analysisId, ct))
            return StatusCode(StatusCodes.Status202Accepted, new { status = "running", message = "Ya hay un refresh en proceso" });

        var actor = User.FindFirst("sub")?.Value;
        await powerStore.MarkRunningAsync(analysisId, actor, ct);
        powerQueue.Enqueue(new PowerHistoryJob(analysisId, actor));
        return StatusCode(StatusCodes.Status202Accepted, new { status = "running" });
    }

    /// <summary>Estado del último refresh de encendido/apagado (para polling del front).</summary>
    [HttpGet("analysis/{analysisId:int}/power-history/status")]
    public async Task<IActionResult> GetPowerHistoryStatus(int analysisId, CancellationToken ct)
    {
        var chk = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!chk.Ok) return Translate(chk);

        var status = await powerStore.GetStatusAsync(analysisId, ct);
        if (status is null) return Ok(new { status = "none" });

        return Ok(new
        {
            status = status.Status,
            started_at = status.StartedAt,
            finished_at = status.FinishedAt,
            summary = ParseJson(status.SummaryJson),
            error = status.Error,
        });
    }

    private static JsonNode? ParseJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonNode.Parse(json); } catch { return null; }
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
