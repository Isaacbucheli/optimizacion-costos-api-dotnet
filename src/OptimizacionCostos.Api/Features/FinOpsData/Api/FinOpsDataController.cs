using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Features.FinOpsData.Api;

/// <summary>
/// Endpoints de administración/consulta de los datos open data del FinOps Toolkit
/// (pricing units, regiones, servicios, tipos de recurso, elegibilidad de RI/SP).
/// </summary>
[ApiController]
[Authorize]
[Route("finops-data")]
public sealed class FinOpsDataController(
    IFinOpsDataRefreshService refresh,
    IFinOpsDataStore store,
    IFinOpsRefData refData,
    IMemoryCache cache,
    IRiDiagnosticsQuery diagnostics,
    IAnalysisAccess access,
    ICoverageQuery coverage) : ControllerBase
{
    [HttpPost("refresh")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var results = await refresh.RefreshAllAsync(ct);
        // Invalida la caché de lookups (TTL 1h en SqlFinOpsRefData) para que status/lookups
        // reflejen los datos recién refrescados sin esperar la expiración.
        cache.Remove("finops:eligibility");
        cache.Remove("finops:regions");
        cache.Remove("finops:resource_types");
        return Ok(new { results });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
        => Ok(await store.GetStatusAsync(ct));

    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups(CancellationToken ct)
    {
        var regions = await refData.GetRegionNamesAsync(ct);
        var types = await refData.GetResourceTypesAsync(ct);
        return Ok(new
        {
            regions,
            resource_types = types.ToDictionary(
                kv => kv.Key,
                kv => new { display_name = kv.Value.DisplayName, service_category = kv.Value.ServiceCategory }),
            service_categories = FinOpsServiceCategoryMap.ByServiceKey,
        });
    }

    [HttpGet("ri-diagnostics")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> RiDiagnostics([FromQuery(Name = "analysis_id")] int analysisId, CancellationToken ct)
    {
        var chk = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!chk.Ok)
            return chk.Result == AccessResult.NotFound
                ? NotFound(new { detail = chk.Detail ?? "Not found" })
                : StatusCode(StatusCodes.Status403Forbidden, new { detail = chk.Detail ?? "Sin acceso" });
        return Ok(await diagnostics.ListAsync(analysisId, ct));
    }

    /// <summary>% del inventario del análisis con cost_result calculado + huecos de calculadora (Fase 1 FinOps Toolkit).</summary>
    [HttpGet("/analysis/{analysisId:int}/coverage")]
    public async Task<IActionResult> Coverage(int analysisId, CancellationToken ct)
    {
        var chk = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!chk.Ok)
            return chk.Result == AccessResult.NotFound
                ? NotFound(new { detail = chk.Detail ?? "Not found" })
                : StatusCode(StatusCodes.Status403Forbidden, new { detail = chk.Detail ?? "Sin acceso" });
        return Ok(await coverage.GetAsync(analysisId, ct));
    }
}
