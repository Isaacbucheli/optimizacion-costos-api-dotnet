using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.AzureIntegration;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Features.Inventory.Api;

/// <summary>
/// Importación de inventario Azure usando el catálogo de servicios. Port de
/// app/routes/azure_import.py (prefix /azure/import).
/// </summary>
[ApiController]
[Authorize]
[Route("azure/import")]
[RequireModule(Modules.Costos)]
public sealed class AzureImportController(
    IInventoryImportService import,
    IAnalysisAccess access,
    ILogger<AzureImportController> logger) : ControllerBase
{
    public sealed record ImportRequest(int? CredentialId, List<string>? Subscriptions, List<string>? Services, bool ReplaceExisting = true);

    // -------------------- POST /azure/import/inventory/{analysisId} --------------------
    [HttpPost("inventory/{analysisId:int}")]
    [RequireModule(Modules.Costos, ModuleAccess.Edit)]
    public async Task<IActionResult> ImportInventory(int analysisId, [FromBody] ImportRequest? payload, CancellationToken ct)
    {
        var chk = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!chk.Ok) return Translate(chk);

        payload ??= new ImportRequest(null, null, null, true);
        var opts = new ImportOptions(payload.CredentialId, payload.Subscriptions, payload.Services, payload.ReplaceExisting);
        try
        {
            return Ok(await import.ImportInventoryAsync(analysisId, opts, ct));
        }
        catch (ImportBadRequestException ex) { return BadRequest(new { detail = ex.Message }); }
        catch (NoActiveCredentialsException ex) { return BadRequest(new { detail = ex.Message }); }
        catch (AnalysisNotFoundException ex) { return NotFound(new { detail = ex.Message }); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Inventory import failed type={Type}", ex.GetType().Name);
            return Problem(statusCode: 500, detail: $"Import failed: {ex.GetType().Name}");
        }
    }

    // -------------------- GET /azure/import/inventory/{analysisId}/summary --------------------
    [HttpGet("inventory/{analysisId:int}/summary")]
    public async Task<IActionResult> Summary(int analysisId, CancellationToken ct)
    {
        var chk = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!chk.Ok) return Translate(chk);
        return Ok(await import.SummaryAsync(analysisId, ct));
    }

    // -------------------- POST /azure/import/discover/{clientId} --------------------
    [HttpPost("discover/{clientId:int}")]
    [RequireModule(Modules.Costos, ModuleAccess.Edit)]
    public async Task<IActionResult> Discover(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        try
        {
            return Ok(await import.DiscoverAsync(clientId, ct));
        }
        catch (ImportBadRequestException ex) { return BadRequest(new { detail = ex.Message }); }
        catch (NoActiveCredentialsException ex) { return BadRequest(new { detail = ex.Message }); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discovery failed type={Type}", ex.GetType().Name);
            return Problem(statusCode: 500, detail: $"Discovery failed: {ex.GetType().Name}");
        }
    }

    // -------------------- GET /azure/import/discovered-services/{clientId} --------------------
    [HttpGet("discovered-services/{clientId:int}")]
    public async Task<IActionResult> ListDiscovered(
        int clientId,
        [FromQuery(Name = "only_uncataloged")] bool onlyUncataloged = false,
        [FromQuery(Name = "only_relevant")] bool onlyRelevant = true,
        CancellationToken ct = default)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        return Ok(await import.ListDiscoveredAsync(clientId, onlyUncataloged, onlyRelevant, ct));
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
