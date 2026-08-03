using System.Text.Json;
using System.Xml;
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
    IBoletinLifecycleStore lifecycle, IBoletinNovedadStore novedades) : ControllerBase
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
        try
        {
            var id = await lifecycle.CreateAsync(fields, ct);
            return Ok(new { message = "Entrada creada", id });
        }
        catch (LifecycleClaveDuplicadaException ex)
        {
            return Conflict(new { detail = ex.Message });
        }
    }

    [HttpPut("lifecycle/{id:int}")]
    [RequireModule(Modules.Boletin, ModuleAccess.Edit)]
    public async Task<IActionResult> UpdateLifecycle(int id, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (fields, error) = BuildLifecycleFields(body, requireCore: false);
        if (error is not null) return BadRequest(new { detail = error });
        if (fields.Count == 0) return BadRequest(new { detail = "Nada que actualizar" });
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

    /// <summary>Campos de texto del catálogo: si el JSON manda otro tipo (número/bool) es un intento
    /// de bypass de validación (ej. mandar end_of_support como epoch numérico), así que se rechaza
    /// explícito en vez de convertir silenciosamente. is_active queda fuera: sigue aceptando bool.</summary>
    private static readonly string[] StringColumns =
        ["clave", "producto", "categoria", "match_field", "match_pattern", "end_of_support", "recomendacion", "learn_more_url"];

    /// <summary>Semántica exclude_unset + whitelist (patrón AlertCatalogController.BuildFields).
    /// internal: testeado directo (patrón BoletinLifecycleStore.ReadSeedEntries) sin host HTTP.</summary>
    internal static (Dictionary<string, object?> Fields, string? Error) BuildLifecycleFields(JsonElement body, bool requireCore)
    {
        if (body.ValueKind != JsonValueKind.Object) return ([], "Cuerpo inválido");
        var fields = new Dictionary<string, object?>();
        foreach (var p in body.EnumerateObject())
        {
            if (!LifecycleColumns.Editable.Contains(p.Name)) continue;
            if (StringColumns.Contains(p.Name) && p.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return ([], $"El campo '{p.Name}' debe ser texto");
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
        if (fields.TryGetValue("learn_more_url", out var lmu) && lmu is string urlStr && !string.IsNullOrEmpty(urlStr) &&
            !(Uri.TryCreate(urlStr, UriKind.Absolute, out var parsedUrl) &&
              (parsedUrl.Scheme == Uri.UriSchemeHttp || parsedUrl.Scheme == Uri.UriSchemeHttps)))
            return ([], "learn_more_url debe ser una URL http(s) absoluta");
        if (requireCore)
            foreach (var req in new[] { "clave", "producto", "categoria", "match_field", "match_pattern", "end_of_support", "recomendacion" })
                if (!fields.ContainsKey(req) || fields[req] is null or "")
                    return ([], $"Falta el campo obligatorio '{req}'");
        return (fields, null);
    }

    // ---- Ingesta GLOBAL de novedades del feed de Azure Updates — GLOBAL, no por cliente ----

    [HttpPost("novedades/ingestar")]
    [RequireModule(Modules.Boletin, ModuleAccess.Edit)]
    public async Task<IActionResult> IngestarNovedades(CancellationToken ct)
    {
        try
        {
            var (nuevas, traducidas) = await novedades.IngestAsync(ct);
            var totalActivas = (await novedades.ListAsync(false, ct)).Count;
            return Ok(new { nuevas, traducidas, total_activas = totalActivas });
        }
        // Cancelación real del cliente (ct la dispara el propio caller, ej. cierra la conexión):
        // propaga tal cual, NUNCA se convierte en un 502 — no es un problema del feed.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // XML truncado (XDocument.Parse), feed inalcanzable (DNS/conexión) o timeout del HttpClient
        // (60s, dispara TaskCanceledException/OperationCanceledException con SU PROPIO token interno,
        // no con ct): son fallos del feed de Microsoft, no del servidor — 502 controlado, nunca un
        // 500 crudo (requisito duro del review de T1).
        catch (Exception ex) when (ex is XmlException or HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(ex, "ingesta de novedades: feed de Azure Updates roto o inalcanzable");
            return Problem(statusCode: StatusCodes.Status502BadGateway,
                detail: "No se pudo leer el feed de Azure Updates. Intenta de nuevo.");
        }
    }

    [HttpGet("novedades")]
    public async Task<IActionResult> ListNovedades([FromQuery(Name = "include_inactive")] bool includeInactive, CancellationToken ct)
        => Ok(await novedades.ListAsync(includeInactive, ct));

    [HttpPut("novedades/{id:int}")]
    [RequireModule(Modules.Boletin, ModuleAccess.Edit)]
    public async Task<IActionResult> UpdateNovedad(int id, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (fields, error) = BuildNovedadFields(body);
        if (error is not null) return BadRequest(new { detail = error });
        if (fields.Count == 0) return BadRequest(new { detail = "Nada que actualizar" });
        return await novedades.UpdateAsync(id, fields, ct)
            ? Ok(new { message = "Novedad actualizada", id })
            : NotFound(new { detail = "Novedad no encontrada" });
    }

    /// <summary>Whitelist estricta (patrón BuildLifecycleFields): SOLO categoria_bit (uno de los 4
    /// valores de NovedadColumns.CategoriasBitValidas) e is_active (bool). Cualquier otro campo del
    /// body (incluidos titulo_es/descripcion_es, que son de la ingesta/traducción, no del consultor)
    /// se ignora en silencio — si termina siendo el único campo, el PUT igual falla más arriba con
    /// "Nada que actualizar", así que no hay bypass silencioso posible.</summary>
    internal static (Dictionary<string, object?> Fields, string? Error) BuildNovedadFields(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return ([], "Cuerpo inválido");
        var fields = new Dictionary<string, object?>();
        foreach (var p in body.EnumerateObject())
        {
            if (!NovedadColumns.Editable.Contains(p.Name)) continue;
            if (p.Name == "categoria_bit")
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                    return ([], "El campo 'categoria_bit' debe ser texto");
                var v = p.Value.GetString()!;
                if (!NovedadColumns.CategoriasBitValidas.Contains(v))
                    return ([], $"categoria_bit debe ser uno de: {string.Join(", ", NovedadColumns.CategoriasBitValidas)}");
                fields["categoria_bit"] = v;
            }
            else // is_active
            {
                if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return ([], "El campo 'is_active' debe ser booleano");
                fields["is_active"] = p.Value.ValueKind == JsonValueKind.True;
            }
        }
        return (fields, null);
    }
}
