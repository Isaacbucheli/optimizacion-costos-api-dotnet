using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Engine;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using OptimizacionCostos.Api.Features.CostEngine.Scenarios;
using CostEngineService = OptimizacionCostos.Api.Features.CostEngine.Engine.CostEngine;

namespace OptimizacionCostos.Api.Features.CostEngine.Api;

/// <summary>
/// Motor de calculo de costos (endpoints REST). Port de app/routes/cost_calculation.py
/// con el control de acceso por cliente de app/access_control.py.
///
/// Rutas EXACTAS (sin prefijo de grupo, igual que el router sin prefix del FastAPI):
///   POST /analysis/{id}/calculate
///   POST /analysis/{id}/scenarios
///   GET  /analysis/{id}/results
///   GET  /analysis/{id}/scenarios
///   PUT  /cost-results/{id}/manual-cost
///   POST /prices/refresh-all        (solo admin)
///   GET  /prices/cache-status
///
/// OMITIDO (depende de modulos Azure no portados): ri-coverage/refresh, power-history/refresh.
/// </summary>
[ApiController]
[Authorize]
public sealed class CostCalculationController(
    IAnalysisAccess access,
    CostEngineService engine,
    ScenarioService scenarios,
    ICostResultsQuery query,
    IPriceCache priceCache,
    ILogger<CostCalculationController> logger) : ControllerBase
{
    // -------------------- POST /analysis/{id}/calculate --------------------

    [HttpPost("analysis/{analysisId:int}/calculate")]
    public async Task<IActionResult> Calculate(
        int analysisId, [FromBody] CalculateRequest? payload, CancellationToken ct)
    {
        payload ??= new CalculateRequest();

        // Validacion de rangos (paridad con ge/le de Pydantic).
        if (payload.ResourceOffset < 0)
            return BadRequest(new { detail = "resource_offset must be >= 0" });
        if (payload.ResourceLimit is { } lim && (lim < 1 || lim > 200))
            return BadRequest(new { detail = "resource_limit must be between 1 and 200" });

        var check = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!check.Ok)
            return Translate(check);

        CalculationSummary summary;
        try
        {
            summary = engine.CalculateAnalysis(
                analysisId,
                payload.ServiceKeys,
                resourceOffset: payload.ResourceOffset,
                resourceLimit: payload.ResourceLimit,
                replaceExisting: payload.ReplaceExisting);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Calculate failed type={Type}", ex.GetType().Name);
            return Problem(statusCode: 500, detail: $"Calculate failed: {ex.GetType().Name}");
        }

        // Se devuelve el summary mas, opcionalmente, "scenarios" / "scenarios_error",
        // igual que el dict del FastAPI. Se proyecta a un diccionario para poder anexarlos.
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["analysis_id"] = summary.AnalysisId,
            ["summary"] = summary.Summary,
        };
        if (summary.Error is not null)
            result["error"] = summary.Error;

        if (payload.AutoBuildScenarios)
        {
            try
            {
                result["scenarios"] = scenarios.BuildScenarios(analysisId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scenarios build failed type={Type}", ex.GetType().Name);
                var msg = ex.Message;
                result["scenarios_error"] = $"{ex.GetType().Name}: {msg[..Math.Min(msg.Length, 300)]}";
            }
        }

        // NOTA: power-history (Activity Log) NO se corre aqui, igual que el Python.
        return Ok(result);
    }

    // -------------------- POST /analysis/{id}/scenarios --------------------

    [HttpPost("analysis/{analysisId:int}/scenarios")]
    public async Task<IActionResult> RecalculateScenarios(int analysisId, CancellationToken ct)
    {
        var check = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!check.Ok)
            return Translate(check);

        try
        {
            return Ok(scenarios.BuildScenarios(analysisId));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scenarios failed type={Type}", ex.GetType().Name);
            return Problem(statusCode: 500, detail: $"Scenarios failed: {ex.GetType().Name}");
        }
    }

    // -------------------- GET /analysis/{id}/results --------------------

    [HttpGet("analysis/{analysisId:int}/results")]
    public async Task<IActionResult> GetResults(
        int analysisId, [FromQuery(Name = "service_key")] string? serviceKey, CancellationToken ct)
    {
        var check = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!check.Ok)
            return Translate(check);

        var rows = await query.GetResultsAsync(analysisId, serviceKey, ct);
        return Ok(rows);
    }

    // -------------------- GET /analysis/{id}/scenarios --------------------

    [HttpGet("analysis/{analysisId:int}/scenarios")]
    public async Task<IActionResult> GetScenarios(int analysisId, CancellationToken ct)
    {
        var check = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!check.Ok)
            return Translate(check);

        var rows = await query.GetScenariosAsync(analysisId, ct);
        return Ok(rows);
    }

    // -------------------- PUT /cost-results/{id}/manual-cost --------------------

    [HttpPut("cost-results/{costResultId:int}/manual-cost")]
    public async Task<IActionResult> SetManualCost(
        int costResultId, [FromBody] ManualCostRequest payload, CancellationToken ct)
    {
        var check = await access.AssertCostResultAccessAsync(User, costResultId, ct);
        if (!check.Ok)
            return Translate(check);

        var updated = await query.SetManualCostAsync(
            costResultId, payload.ManualMonthlyCost, payload.ManualCostNote, ct);
        if (!updated)
            return NotFound(new { detail = "cost_result not found" });

        return Ok(new { message = "Costo manual actualizado", cost_result_id = costResultId });
    }

    // -------------------- POST /prices/refresh-all (solo admin) --------------------

    [HttpPost("prices/refresh-all")]
    [Authorize(Roles = Roles.Admin)]
    public IActionResult RefreshAllPrices()
    {
        var rows = priceCache.ClearAll();
        return Ok(new { message = "Cache de precios limpiado", removed_rows = rows });
    }

    // -------------------- GET /prices/cache-status --------------------

    [HttpGet("prices/cache-status")]
    public IActionResult CacheStatus() => Ok(priceCache.GetStatus());

    // -------------------- helpers --------------------

    /// <summary>Traduce el resultado del control de acceso a una respuesta HTTP.</summary>
    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden,
            new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
