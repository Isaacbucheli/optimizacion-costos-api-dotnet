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
    IBoletinLifecycleStore lifecycle, IBoletinMigracionStore migracion, IBoletinNovedadStore novedades,
    IBoletinNovedadClienteStore novedadesCliente) : ControllerBase
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

    // ---- Catálogo de rutas de migración — GLOBAL, no por cliente (Fase 2 Entrega 4, Task 3) ----

    [HttpGet("migracion")]
    public async Task<IActionResult> ListMigracion([FromQuery(Name = "include_inactive")] bool includeInactive, CancellationToken ct)
        => Ok(await migracion.ListAsync(includeInactive, ct));

    [HttpPost("migracion")]
    [RequireModule(Modules.Boletin, ModuleAccess.Edit)]
    public async Task<IActionResult> CreateMigracion([FromBody] JsonElement body, CancellationToken ct)
    {
        var (fields, error) = BuildMigracionFields(body, requireCore: true);
        if (error is not null) return BadRequest(new { detail = error });
        try
        {
            var id = await migracion.CreateAsync(fields, ct);
            return Ok(new { message = "Ruta creada", id });
        }
        catch (MigracionClaveDuplicadaException ex)
        {
            return Conflict(new { detail = ex.Message });
        }
    }

    [HttpPut("migracion/{id:int}")]
    [RequireModule(Modules.Boletin, ModuleAccess.Edit)]
    public async Task<IActionResult> UpdateMigracion(int id, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (fields, error) = BuildMigracionFields(body, requireCore: false);
        if (error is not null) return BadRequest(new { detail = error });
        if (fields.Count == 0) return BadRequest(new { detail = "Nada que actualizar" });
        return await migracion.UpdateAsync(id, fields, ct)
            ? Ok(new { message = "Ruta actualizada", id })
            : NotFound(new { detail = "Ruta no encontrada" });
    }

    [HttpDelete("migracion/{id:int}")]
    [RequireModule(Modules.Boletin, ModuleAccess.Edit)]
    public async Task<IActionResult> DeleteMigracion(int id, CancellationToken ct)
        => await migracion.SoftDeleteAsync(id, ct)
            ? Ok(new { message = "Ruta desactivada", id })
            : NotFound(new { detail = "Ruta no encontrada" });

    /// <summary>Sugerencias IA EFÍMERAS de rutas de migración para los anuncios sin ruta del cliente
    /// (Fase 2 Entrega 4, Task 4): nada se persiste, son borradores para que el consultor los revise
    /// y guarde a mano vía el CRUD de arriba si le parecen buenos. IA apagada o reintentos agotados
    /// (InvalidOperationException) → 503 con detail, nunca un 500 crudo — mismo mapeo que
    /// EvaluarNovedadesCliente.</summary>
    [HttpPost("clients/{clientId:int}/migracion/sugerir")]
    [RequireModule(Modules.Boletin, ModuleAccess.Edit)]
    public async Task<IActionResult> SugerirMigracion(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        try { return Ok(await svc.SugerirMigracionAsync(clientId, ct)); }
        catch (InvalidOperationException ex)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
        }
    }

    /// <summary>Campos de texto del catálogo de migración: mismo criterio anti-bypass de
    /// StringColumns/BuildLifecycleFields (un número/bool en un campo de texto se rechaza explícito
    /// en vez de convertirse en silencio). is_active queda fuera: sigue aceptando bool.</summary>
    private static readonly string[] MigracionStringColumns =
        ["clave", "desde", "hacia", "notas", "match_pattern", "learn_more_url"];

    /// <summary>Espejo EXACTO de BuildLifecycleFields con las columnas de migración: misma semántica
    /// exclude_unset + whitelist, misma normalización de match_pattern (trim + minúsculas, porque
    /// BoletinMigracion.MatchText compara en minúsculas) y misma validación de learn_more_url
    /// http(s) absoluta. Sin categoria/match_field/end_of_support (esas son propias de lifecycle).</summary>
    internal static (Dictionary<string, object?> Fields, string? Error) BuildMigracionFields(JsonElement body, bool requireCore)
    {
        if (body.ValueKind != JsonValueKind.Object) return ([], "Cuerpo inválido");
        var fields = new Dictionary<string, object?>();
        foreach (var p in body.EnumerateObject())
        {
            if (!MigracionColumns.Editable.Contains(p.Name)) continue;
            if (MigracionStringColumns.Contains(p.Name) && p.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
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
        if (fields.TryGetValue("match_pattern", out var mp) && mp is string pat)
            fields["match_pattern"] = pat.Trim().ToLowerInvariant(); // los patrones se comparan en minúsculas
        if (fields.TryGetValue("learn_more_url", out var lmu) && lmu is string urlStr && !string.IsNullOrEmpty(urlStr) &&
            !(Uri.TryCreate(urlStr, UriKind.Absolute, out var parsedUrl) &&
              (parsedUrl.Scheme == Uri.UriSchemeHttp || parsedUrl.Scheme == Uri.UriSchemeHttps)))
            return ([], "learn_more_url debe ser una URL http(s) absoluta");
        if (requireCore)
            foreach (var req in new[] { "clave", "desde", "hacia", "notas", "match_pattern" })
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
            var totalActivas = await novedades.CountActivasAsync(ct);
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

    // ---- Evaluación de novedades POR CLIENTE (Fase 2 Entrega 3, Task 4) ----

    [HttpPost("clients/{clientId:int}/novedades/evaluar")]
    [RequireModule(Modules.Boletin, ModuleAccess.Edit)]
    public async Task<IActionResult> EvaluarNovedadesCliente(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        try
        {
            var (evaluadas, candidatas) = await novedadesCliente.EvaluarPendientesAsync(clientId, ct);
            return Ok(new { evaluadas, candidatas });
        }
        // Cero suscripciones administradas: problema de CONFIGURACIÓN del cliente, no transitorio
        // (mismo mapeo que BoletinController.Sync con el sync general del Boletín) → 400.
        catch (BoletinNoManagedSubscriptionsException ex)
        {
            return BadRequest(new { detail = ex.Message });
        }
        // IA no configurada, o inventario ilegible en TODAS las credenciales administradas (existiendo
        // al menos una): ambos son "no se puede evaluar ahora mismo" (mensaje del store), nunca un 500 crudo.
        catch (InvalidOperationException ex)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
        }
    }

    [HttpGet("clients/{clientId:int}/novedades")]
    public async Task<IActionResult> ListNovedadesCliente(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        var rows = await novedadesCliente.ListAsync(clientId, ct);
        return Ok(new
        {
            aprobadas = rows.Where(r => r.Estado.Estado == NovedadClienteEstados.Aprobada).Select(NovedadClienteItem).ToList(),
            pendientes = rows.Where(r => r.Estado.Estado == NovedadClienteEstados.Pendiente).Select(NovedadClienteItem).ToList(),
        });
    }

    [HttpPut("novedades-cliente/{id:int}")]
    [RequireModule(Modules.Boletin, ModuleAccess.Edit)]
    public async Task<IActionResult> DecidirNovedadCliente(int id, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (estado, porQue, setPorQue, error) = ParseDecidirNovedadCliente(body);
        if (error is not null) return BadRequest(new { detail = error });

        // Verifica pertenencia ANTES de mutar (patrón FindingStateOwner de OptimizationController):
        // el PUT solo recibe el id de la fila, así que primero se resuelve su client_id dueño y
        // recién ahí se valida el acceso del actor a ESE cliente.
        var ownerClientId = await novedadesCliente.OwnerClientIdAsync(id, ct);
        if (ownerClientId is null) return NotFound(new { detail = "Novedad de cliente no encontrada" });
        var chk = await access.AssertClientAccessAsync(User, ownerClientId.Value, ct);
        if (!chk.Ok) return Translate(chk);

        var actor = User.FindFirst("sub")?.Value ?? "";
        var ok = await novedadesCliente.DecidirAsync(id, ownerClientId.Value, estado!, porQue, setPorQue, actor, ct);
        return ok
            ? Ok(new { message = "Decisión registrada", id, estado })
            // También cubre el id de una fila ya en no_aplica (terminal, ver DecidirAsync): existe
            // pero DecidirAsync no la muta, así que desde acá se ve idéntico a "no encontrada".
            : NotFound(new { detail = "Novedad de cliente no encontrada" });
    }

    /// <summary>Body como JsonElement (patrón BuildLifecycleFields/BuildNovedadFields de este mismo
    /// controller) en vez de un DTO tipado: la diferencia entre "por_que ausente" (se conserva el
    /// valor actual, típicamente el texto que puso la IA al evaluar) y "por_que presente pero null"
    /// (se borra explícitamente) solo se puede distinguir inspeccionando qué propiedades trae el JSON
    /// crudo — un DTO con `string? PorQue` colapsaría ambos casos en null.</summary>
    internal static (string? Estado, string? PorQue, bool SetPorQue, string? Error) ParseDecidirNovedadCliente(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return (null, null, false, "Cuerpo inválido");
        try
        {
            string? estado = body.TryGetProperty("estado", out var estadoEl) && estadoEl.ValueKind == JsonValueKind.String
                ? estadoEl.GetString()
                : null;
            if (estado is null || !NovedadClienteEstados.DecidiblesValidos.Contains(estado))
                return (null, null, false, $"estado debe ser uno de: {string.Join(", ", NovedadClienteEstados.DecidiblesValidos)}");

            // Mismo rechazo de tipos que BuildLifecycleFields/BuildNovedadFields: un por_que numérico
            // (JSON bien formado) llegaba hasta GetString() y reventaba en 500 crudo.
            var setPorQue = body.TryGetProperty("por_que", out var porQueEl);
            if (setPorQue && porQueEl.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return (null, null, false, "El campo 'por_que' debe ser texto o null");
            string? porQue = setPorQue && porQueEl.ValueKind != JsonValueKind.Null ? porQueEl.GetString() : null;

            return (estado, porQue, setPorQue, null);
        }
        // JsonDocument NO valida el UTF-8 de los strings al parsear: lo difiere hasta GetString(),
        // que lanza InvalidOperationException ante bytes inválidos (cliente con encoding roto).
        // Cuerpo malformado = 400, nunca un 500 crudo.
        catch (InvalidOperationException)
        {
            return (null, null, false, "Cuerpo inválido");
        }
    }

    /// <summary>Shape snake_case exacto pedido para el GET por cliente: `published_at` (no
    /// `published_at_utc` como el NovedadRow crudo del GET global) para mantener el contrato simple
    /// del front.</summary>
    private static object NovedadClienteItem((NovedadRow Novedad, NovedadClienteRow Estado) r) => new
    {
        id = r.Estado.Id,
        novedad_id = r.Novedad.Id,
        titulo = r.Novedad.Titulo,
        titulo_es = r.Novedad.TituloEs,
        descripcion = r.Novedad.Descripcion,
        descripcion_es = r.Novedad.DescripcionEs,
        link = r.Novedad.Link,
        estado_feed = r.Novedad.EstadoFeed,
        categoria_bit = r.Novedad.CategoriaBit,
        published_at = r.Novedad.PublishedAtUtc,
        por_que = r.Estado.PorQue,
        estado = r.Estado.Estado,
    };
}
