using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Features.Cdc.Api;

/// <summary>
/// Gestión CDC: reservas Azure por cliente (días restantes, utilización, consumers).
/// Port de app/routes/cdc.py (prefix /cdc). Solo lectura; reutiliza credenciales del cliente.
/// </summary>
[ApiController]
[Authorize]
[Route("cdc")]
[RequireModule(Modules.Reservations)]
public sealed class CdcController(
    IReservationService reservations,
    IAzureReservationsClient client,
    IAnalysisAccess access,
    ISqlConnectionFactory factory,
    ILogger<CdcController> logger) : ControllerBase
{
    [HttpGet("clients/{clientId:int}/reservations")]
    public async Task<IActionResult> GetReservations(
        int clientId, [FromQuery(Name = "alert_days")] int alertDays = 30, [FromQuery(Name = "include_utilization")] bool includeUtilization = true, CancellationToken ct = default)
    {
        if (alertDays is < 1 or > 365) return BadRequest(new { detail = "alert_days fuera de rango (1-365)" });
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var creds = await reservations.ActiveCredentialsAsync(clientId, ct);
        if (creds.Count == 0)
            return Ok(new Dictionary<string, object?>
            {
                ["client_id"] = clientId, ["alert_days"] = alertDays, ["has_credentials"] = false,
                ["total"] = 0, ["expiring_count"] = 0, ["expired_count"] = 0,
                ["reservations"] = Array.Empty<object>(), ["errors"] = Array.Empty<object>(),
                ["message"] = "El cliente no tiene credenciales Azure activas.",
            });

        try
        {
            var result = new Dictionary<string, object?>(await reservations.ListClientReservationsAsync(creds, alertDays, includeUtilization, ct))
            {
                ["client_id"] = clientId, ["has_credentials"] = true,
            };
            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error leyendo reservas client_id={Cid}", clientId);
            return Problem(statusCode: 500, detail: "Error interno del servidor");
        }
    }

    [HttpGet("clients/{clientId:int}/reservation-consumers")]
    public async Task<IActionResult> GetConsumers(
        int clientId, [FromQuery(Name = "credential_id")] int credentialId, [FromQuery(Name = "reservation_id")] string reservationId,
        [FromQuery] int days = 30, CancellationToken ct = default)
    {
        if (days is < 1 or > 90) return BadRequest(new { detail = "days fuera de rango (1-90)" });
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        if (!await CredentialBelongsAsync(credentialId, clientId, ct))
            return NotFound(new { detail = "Credencial no encontrada para el cliente" });
        if (!(reservationId ?? "").ToLowerInvariant().Contains("/providers/microsoft.capacity/reservationorders/"))
            return BadRequest(new { detail = "reservation_id invalido" });

        var subNames = await SubscriptionNamesAsync(clientId, ct);
        try
        {
            var consumers = await client.GetConsumersAsync(credentialId, reservationId!, days, ct);
            var withNames = consumers.Select(c => new
            {
                instance_id = c.InstanceId, resource_name = c.ResourceName, resource_group = c.ResourceGroup,
                subscription_id = c.SubscriptionId,
                subscription_name = c.SubscriptionId is not null && subNames.TryGetValue(c.SubscriptionId.ToLowerInvariant(), out var n) ? n : null,
                sku_name = c.SkuName, used_hours = c.UsedHours, last_seen = c.LastSeen, days_seen = c.DaysSeen,
            }).ToList();
            return Ok(new { consumers = withNames, source = withNames.Count > 0 ? "consumption" : "empty", count = withNames.Count, days });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "reservationDetails falló client_id={Cid} type={Type}", clientId, ex.GetType().Name);
            return Ok(new { consumers = Array.Empty<object>(), source = "error", days, count = 0 });
        }
    }

    [HttpGet("clients/{clientId:int}/reservation-utilization")]
    public async Task<IActionResult> GetUtilization(
        int clientId, [FromQuery(Name = "credential_id")] int credentialId, [FromQuery(Name = "reservation_id")] string reservationId, CancellationToken ct = default)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        if (!await CredentialBelongsAsync(credentialId, clientId, ct))
            return NotFound(new { detail = "Credencial no encontrada para el cliente" });
        if (!(reservationId ?? "").ToLowerInvariant().Contains("/providers/microsoft.capacity/"))
            return BadRequest(new { detail = "reservation_id invalido" });

        try
        {
            var (last, avg7d) = await client.GetUtilizationAsync(credentialId, reservationId!, ct);
            return Ok(new { utilization_last = last, utilization_7d = avg7d });
        }
        catch
        {
            logger.LogInformation("Utilizacion no disponible client_id={Cid}", clientId);
            return Ok(new { utilization_last = "n/d", utilization_7d = "n/d" });
        }
    }

    private async Task<bool> CredentialBelongsAsync(int credentialId, int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dbo.client_azure_credentials WHERE credential_id = @cred AND client_id = @cid AND is_active = 1";
        cmd.Parameters.Add(new SqlParameter("@cred", credentialId));
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private async Task<Dictionary<string, string?>> SubscriptionNamesAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT subscription_id, subscription_name FROM dbo.client_azure_subscriptions WHERE client_id = @cid";
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var map = new Dictionary<string, string?>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (!r.IsDBNull(0)) map[r.GetString(0).ToLowerInvariant()] = r.IsDBNull(1) ? null : r.GetString(1);
        return map;
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
