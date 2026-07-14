using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;

namespace OptimizacionCostos.Api.Features.AlertCatalog;

/// <summary>
/// CRUD del catalogo de alertas Azure Monitor. Equivale a app/routes/alerts.py.
/// Ver -> permiso "alerts" del perfil; crear/editar/eliminar -> permiso de edición (lector nunca).
/// </summary>
[ApiController]
[Route("alert-catalog")]
[Authorize]
[RequireModule(Modules.Alerts)]
public sealed class AlertCatalogController(IAlertCatalogStore store) : ControllerBase
{
    // ---- Biblioteca KQL ----

    [HttpGet("kql")]
    public async Task<IReadOnlyList<KqlItem>> ListKql(CancellationToken ct)
        => await store.ListKqlAsync(ct: ct);

    [HttpPost("kql")]
    [RequireModule(Modules.Alerts, ModuleAccess.Edit)]
    public async Task<IActionResult> CreateKql([FromBody] KqlCreate payload, CancellationToken ct)
    {
        var id = await store.CreateKqlAsync(payload, ct);
        return Ok(new { message = "Consulta KQL creada", kql_id = id });
    }

    [HttpPut("kql/{kqlId:int}")]
    [RequireModule(Modules.Alerts, ModuleAccess.Edit)]
    public async Task<IActionResult> UpdateKql(int kqlId, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (fields, error) = BuildFields(body, AlertColumns.Kql, nameMaxLength: 200);
        if (error is not null) return BadRequest(new { detail = error });
        if (fields.Count == 0) return BadRequest(new { detail = "No fields to update" });
        if (!await store.UpdateKqlAsync(kqlId, fields, ct))
            return NotFound(new { detail = "KQL query not found" });
        return Ok(new { message = "Consulta KQL actualizada", kql_id = kqlId });
    }

    [HttpDelete("kql/{kqlId:int}")]
    [RequireModule(Modules.Alerts, ModuleAccess.Edit)]
    public async Task<IActionResult> DeleteKql(int kqlId, CancellationToken ct)
    {
        if (!await store.SoftDeleteKqlAsync(kqlId, ct))
            return NotFound(new { detail = "KQL query not found" });
        return Ok(new { message = "Consulta KQL desactivada", kql_id = kqlId });
    }

    // ---- Alertas ----

    [HttpGet]
    public async Task<IReadOnlyList<AlertItem>> ListAlerts(CancellationToken ct)
        => await store.ListAlertsAsync(ct: ct);

    [HttpPost]
    [RequireModule(Modules.Alerts, ModuleAccess.Edit)]
    public async Task<IActionResult> CreateAlert([FromBody] AlertCreate payload, CancellationToken ct)
    {
        var id = await store.CreateAlertAsync(payload, ct);
        return Ok(new { message = "Alerta creada", alert_id = id });
    }

    [HttpGet("{alertId:int}")]
    public async Task<IActionResult> GetAlert(int alertId, CancellationToken ct)
    {
        var alert = await store.GetAlertAsync(alertId, ct);
        return alert is null ? NotFound(new { detail = "Alert not found" }) : Ok(alert);
    }

    [HttpPut("{alertId:int}")]
    [RequireModule(Modules.Alerts, ModuleAccess.Edit)]
    public async Task<IActionResult> UpdateAlert(int alertId, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (fields, error) = BuildFields(body, AlertColumns.Alert, nameMaxLength: 300);
        if (error is not null) return BadRequest(new { detail = error });
        if (fields.Count == 0) return BadRequest(new { detail = "No fields to update" });
        if (!await store.UpdateAlertAsync(alertId, fields, ct))
            return NotFound(new { detail = "Alert not found" });
        return Ok(new { message = "Alerta actualizada", alert_id = alertId });
    }

    [HttpDelete("{alertId:int}")]
    [RequireModule(Modules.Alerts, ModuleAccess.Edit)]
    public async Task<IActionResult> DeleteAlert(int alertId, CancellationToken ct)
    {
        if (!await store.SoftDeleteAlertAsync(alertId, ct))
            return NotFound(new { detail = "Alert not found" });
        return Ok(new { message = "Alerta desactivada", alert_id = alertId });
    }

    /// <summary>
    /// Construye el diccionario de campos a actualizar tomando solo las claves
    /// presentes en el body que esten en la whitelist (semantica exclude_unset).
    /// Valida `name` si viene presente.
    /// </summary>
    private static (Dictionary<string, object?> Fields, string? Error) BuildFields(
        JsonElement body, string[] allowed, int nameMaxLength)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (body.ValueKind != JsonValueKind.Object)
            return (fields, "Invalid body");

        foreach (var col in allowed)
        {
            if (!body.TryGetProperty(col, out var el)) continue;
            var value = AlertCatalogSchema.JsonToDb(el);

            if (col == "name")
            {
                var name = value as string;
                if (string.IsNullOrEmpty(name) || name.Length > nameMaxLength)
                    return (fields, "Invalid name");
            }
            fields[col] = value;
        }
        return (fields, null);
    }
}
