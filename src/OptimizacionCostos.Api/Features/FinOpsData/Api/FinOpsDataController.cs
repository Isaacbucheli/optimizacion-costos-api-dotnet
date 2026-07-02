using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using OptimizacionCostos.Api.Auth;

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
    IMemoryCache cache) : ControllerBase
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
}
