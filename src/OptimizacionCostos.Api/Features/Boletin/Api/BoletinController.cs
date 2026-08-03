using System.Text.Json;
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
    IBoletinService svc, IAnalysisAccess access, ILogger<BoletinController> logger,
    IBoletinLifecycleStore lifecycle) : ControllerBase
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

    // ---- Catálogo de lifecycle (fin de soporte) — GLOBAL, no por cliente ----

    [HttpGet("lifecycle")]
    public async Task<IActionResult> ListLifecycle([FromQuery(Name = "include_inactive")] bool includeInactive, CancellationToken ct)
        => Ok(await lifecycle.ListAsync(includeInactive, ct));

    [HttpPost("lifecycle")]
    [RequireModule(Modules.Boletin, ModuleAccess.Edit)]
    public async Task<IActionResult> CreateLifecycle([FromBody] JsonElement body, CancellationToken ct)
    {
        var (fields, error) = BuildLifecycleFields(body, requireCore: true);
        if (error is not null) return BadRequest(new { detail = error });
        var id = await lifecycle.CreateAsync(fields, ct);
        return Ok(new { message = "Entrada creada", id });
    }

    [HttpPut("lifecycle/{id:int}")]
    [RequireModule(Modules.Boletin, ModuleAccess.Edit)]
    public async Task<IActionResult> UpdateLifecycle(int id, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (fields, error) = BuildLifecycleFields(body, requireCore: false);
        if (error is not null) return BadRequest(new { detail = error });
        return await lifecycle.UpdateAsync(id, fields, ct)
            ? Ok(new { message = "Entrada actualizada", id })
            : NotFound(new { detail = "Entrada no encontrada" });
    }

    [HttpDelete("lifecycle/{id:int}")]
    [RequireModule(Modules.Boletin, ModuleAccess.Edit)]
    public async Task<IActionResult> DeleteLifecycle(int id, CancellationToken ct)
        => await lifecycle.SoftDeleteAsync(id, ct)
            ? Ok(new { message = "Entrada desactivada", id })
            : NotFound(new { detail = "Entrada no encontrada" });

    /// <summary>Semántica exclude_unset + whitelist (patrón AlertCatalogController.BuildFields).</summary>
    private static (Dictionary<string, object?> Fields, string? Error) BuildLifecycleFields(JsonElement body, bool requireCore)
    {
        if (body.ValueKind != JsonValueKind.Object) return ([], "Cuerpo inválido");
        var fields = new Dictionary<string, object?>();
        foreach (var p in body.EnumerateObject())
        {
            if (!LifecycleColumns.Editable.Contains(p.Name)) continue;
            fields[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => p.Value.GetDouble(),
                _ => p.Value.GetString(),
            };
        }
        if (fields.TryGetValue("categoria", out var cat) && cat is string c && c != "so" && c != "bd")
            return ([], "categoria debe ser 'so' o 'bd'");
        if (fields.TryGetValue("match_field", out var mf) && mf is string f && f != "os_name" && f != "sql_image_offer")
            return ([], "match_field debe ser 'os_name' o 'sql_image_offer'");
        if (fields.TryGetValue("match_pattern", out var mp) && mp is string pat)
            fields["match_pattern"] = pat.Trim().ToLowerInvariant(); // los patrones se comparan en minúsculas
        if (fields.TryGetValue("end_of_support", out var eos) && eos is string d && !DateOnly.TryParse(d, out _))
            return ([], "end_of_support debe ser fecha yyyy-MM-dd");
        if (requireCore)
            foreach (var req in new[] { "clave", "producto", "categoria", "match_field", "match_pattern", "end_of_support", "recomendacion" })
                if (!fields.ContainsKey(req) || fields[req] is null or "")
                    return ([], $"Falta el campo obligatorio '{req}'");
        return (fields, null);
    }
}
