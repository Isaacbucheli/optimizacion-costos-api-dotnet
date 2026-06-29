using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Features.AzureIntegration.Api;

/// <summary>
/// Gestión y sincronización de suscripciones Azure por cliente. Port de
/// app/routes/azure_subscriptions.py (prefix /azure/subscriptions). El filtro
/// is_active AND is_managed es base de costos/informes; is_managed lo decide el usuario.
/// </summary>
[ApiController]
[Authorize]
[Route("azure/subscriptions")]
public sealed class AzureSubscriptionsController(
    IClientSubscriptionStore store,
    ISubscriptionSyncService sync,
    IAnalysisAccess access,
    ILogger<AzureSubscriptionsController> logger) : ControllerBase
{
    public sealed record SubscriptionCreateRequest(int ClientId, int CredentialId, string? SubscriptionId, string? SubscriptionName);
    public sealed record SubscriptionUpdateRequest(string? SubscriptionName, bool? IsActive, bool? IsManaged);

    // -------------------- GET /azure/subscriptions?client_id= --------------------
    [HttpGet]
    public async Task<IActionResult> List([FromQuery(Name = "client_id")] int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        return Ok(await store.ListByClientAsync(clientId, ct));
    }

    // -------------------- POST /azure/subscriptions/sync-client/{clientId} --------------------
    [HttpPost("sync-client/{clientId:int}")]
    public async Task<IActionResult> SyncClient(int clientId, CancellationToken ct)
    {
        try
        {
            var summary = await sync.SyncAsync(clientId, ct);
            // Si NINGUNA suscripción se sincronizó y hubo errores → 502 (paridad con FastAPI).
            if (summary.ActiveTotal == 0 && summary.Errors.Count > 0)
                return StatusCode(StatusCodes.Status502BadGateway,
                    new { message = "No subscriptions could be synchronized", errors = summary.Errors });
            return Ok(summary);
        }
        catch (NoActiveCredentialsException ex)
        {
            return BadRequest(new { detail = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subscription sync error type={Type}", ex.GetType().Name);
            return Problem(statusCode: 500, detail: "Error interno del servidor");
        }
    }

    // -------------------- POST /azure/subscriptions --------------------
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SubscriptionCreateRequest payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload.SubscriptionId))
            return BadRequest(new { detail = "subscription_id es requerido" });

        var owner = await store.ClientIdForCredentialAsync(payload.CredentialId, ct);
        if (owner is null) return NotFound(new { detail = "Credencial no encontrada" });
        if (owner.Value != payload.ClientId)
            return BadRequest(new { detail = "La credencial no pertenece al cliente indicado" });

        var newId = await store.InsertManualAsync(payload.ClientId, payload.CredentialId, payload.SubscriptionId!, payload.SubscriptionName, ct);
        return Ok(new { message = "Suscripción registrada correctamente", client_subscription_id = newId });
    }

    // -------------------- PUT /azure/subscriptions/{id} --------------------
    [HttpPut("{clientSubscriptionId:int}")]
    public async Task<IActionResult> Update(int clientSubscriptionId, [FromBody] SubscriptionUpdateRequest payload, CancellationToken ct)
    {
        if (payload.SubscriptionName is null && payload.IsActive is null && payload.IsManaged is null)
            return BadRequest(new { detail = "Nada que actualizar" });

        var ok = await store.UpdateAsync(clientSubscriptionId, payload.SubscriptionName, payload.IsActive, payload.IsManaged, ct);
        if (!ok) return NotFound(new { detail = "Suscripción no encontrada" });
        return Ok(new { message = "Suscripción actualizada correctamente" });
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
