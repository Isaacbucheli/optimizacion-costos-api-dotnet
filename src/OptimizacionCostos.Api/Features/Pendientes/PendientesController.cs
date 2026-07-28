using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Pendientes;

/// <summary>
/// Tablero de pendientes y bloqueantes por cliente (Seguimiento CDC). Dos áreas = dos módulos de
/// permiso independientes: alguien puede ver Infra y no CDC.
///
/// <see cref="RequireModuleAttribute"/> no sirve acá porque es estático y el módulo depende del
/// <c>{area}</c> de la ruta, así que el check va explícito por <see cref="IModulePermissionService"/>
/// (mismas reglas: admin pasa siempre, lector nunca edita, fila ausente = denegado).
///
/// El dato vive en OTRA base (la del tablero, que la SWA sigue usando), de ahí el 503 cuando no está
/// configurada y la concurrencia optimista en la edición.
/// </summary>
[ApiController]
[Route("pendientes")]
[Authorize]
public sealed class PendientesController(
    IPendientesStore store,
    ISeguimientoSqlConnectionFactory factory,
    IModulePermissionService permissions) : ControllerBase
{
    // ---------- Lectura ----------

    /// <summary>Payload completo del área: catálogo de clientes + pendientes con su historial.</summary>
    [HttpGet("{area}")]
    public async Task<IActionResult> GetArea(string area, CancellationToken ct)
    {
        var (resolved, error) = await GuardAsync(area, edit: false, ct);
        if (error is not null) return error;
        return Ok(await store.GetAreaAsync(resolved!, ct));
    }

    [HttpGet("{area}/items/{id}")]
    public async Task<IActionResult> GetItem(string area, string id, CancellationToken ct)
    {
        var (resolved, error) = await GuardAsync(area, edit: false, ct);
        if (error is not null) return error;

        var item = await store.GetItemAsync(resolved!, id, ct);
        return item is null ? NotFound(new { detail = "Pendiente no encontrado" }) : Ok(item);
    }

    // ---------- Pendientes ----------

    [HttpPost("{area}/items")]
    public async Task<IActionResult> CreateItem(string area, [FromBody] PendienteWrite body, CancellationToken ct)
    {
        var (resolved, error) = await GuardAsync(area, edit: true, ct);
        if (error is not null) return error;

        var (data, invalid) = Validate(body, requireToken: false);
        if (invalid is not null) return BadRequest(new { detail = invalid });

        if (!await store.ClienteExistsAsync(resolved!, data!.ClienteNum, ct))
            return BadRequest(new { detail = "El cliente no existe en esta área" });

        var id = await store.CreateItemAsync(resolved!, data, ct);
        return Ok(new { message = "Pendiente creado", id });
    }

    [HttpPut("{area}/items/{id}")]
    public async Task<IActionResult> UpdateItem(
        string area, string id, [FromBody] PendienteWrite body, CancellationToken ct)
    {
        var (resolved, error) = await GuardAsync(area, edit: true, ct);
        if (error is not null) return error;

        var (data, invalid) = Validate(body, requireToken: true);
        if (invalid is not null) return BadRequest(new { detail = invalid });

        if (!await store.ClienteExistsAsync(resolved!, data!.ClienteNum, ct))
            return BadRequest(new { detail = "El cliente no existe en esta área" });

        return await store.UpdateItemAsync(resolved!, id, data, ct) switch
        {
            WriteOutcome.Ok => Ok(new { message = "Pendiente actualizado", id }),
            WriteOutcome.NotFound => NotFound(new { detail = "Pendiente no encontrado" }),
            _ => Conflict(new { detail = "Alguien más cambió este pendiente. Recargue para ver la última versión." }),
        };
    }

    [HttpDelete("{area}/items/{id}")]
    public async Task<IActionResult> DeleteItem(string area, string id, CancellationToken ct)
    {
        var (resolved, error) = await GuardAsync(area, edit: true, ct);
        if (error is not null) return error;

        return await store.DeleteItemAsync(resolved!, id, ct)
            ? Ok(new { message = "Pendiente eliminado", id })
            : NotFound(new { detail = "Pendiente no encontrado" });
    }

    // ---------- Notas ----------

    [HttpPost("{area}/items/{id}/notas")]
    public async Task<IActionResult> AddNota(
        string area, string id, [FromBody] NotaWrite body, CancellationToken ct)
    {
        var (resolved, error) = await GuardAsync(area, edit: true, ct);
        if (error is not null) return error;

        if (string.IsNullOrWhiteSpace(body.Nota))
            return BadRequest(new { detail = "La nota no puede estar vacía" });

        // El autor lo pone el backend: el del body se ignora a propósito.
        var autor = User.FindFirst("name")?.Value ?? User.FindFirst("sub")?.Value;
        var histId = await store.AddNotaAsync(resolved!, id, body with { Nota = body.Nota.Trim() }, autor, ct);

        return histId is null
            ? NotFound(new { detail = "Pendiente no encontrado" })
            : Ok(new { message = "Nota agregada", hist_id = histId });
    }

    [HttpDelete("{area}/items/{id}/notas/{histId:int}")]
    public async Task<IActionResult> DeleteNota(string area, string id, int histId, CancellationToken ct)
    {
        var (resolved, error) = await GuardAsync(area, edit: true, ct);
        if (error is not null) return error;

        return await store.DeleteNotaAsync(resolved!, id, histId, ct)
            ? Ok(new { message = "Nota eliminada", hist_id = histId })
            : NotFound(new { detail = "Nota no encontrada" });
    }

    // ---------- Catálogo de clientes ----------
    // La lista se sirve dentro del payload de GET /pendientes/{area}; acá solo van las mutaciones.

    [HttpPost("{area}/clientes")]
    public async Task<IActionResult> CreateCliente(
        string area, [FromBody] ClienteWrite body, CancellationToken ct)
    {
        var (resolved, error) = await GuardAsync(area, edit: true, ct);
        if (error is not null) return error;

        var (data, invalid) = ValidateCliente(body);
        if (invalid is not null) return BadRequest(new { detail = invalid });

        var num = await store.CreateClienteAsync(resolved!, data!, ct);
        return Ok(new { message = "Cliente creado", num });
    }

    [HttpPut("{area}/clientes/{num:int}")]
    public async Task<IActionResult> UpdateCliente(
        string area, int num, [FromBody] ClienteWrite body, CancellationToken ct)
    {
        var (resolved, error) = await GuardAsync(area, edit: true, ct);
        if (error is not null) return error;

        var (data, invalid) = ValidateCliente(body);
        if (invalid is not null) return BadRequest(new { detail = invalid });

        return await store.UpdateClienteAsync(resolved!, num, data!, ct)
            ? Ok(new { message = "Cliente actualizado", num })
            : NotFound(new { detail = "Cliente no encontrado" });
    }

    [HttpDelete("{area}/clientes/{num:int}")]
    public async Task<IActionResult> DeleteCliente(string area, int num, CancellationToken ct)
    {
        var (resolved, error) = await GuardAsync(area, edit: true, ct);
        if (error is not null) return error;

        return await store.DeleteClienteAsync(resolved!, num, ct) switch
        {
            ClienteDeleteOutcome.Ok => Ok(new { message = "Cliente eliminado", num }),
            ClienteDeleteOutcome.NotFound => NotFound(new { detail = "Cliente no encontrado" }),
            _ => Conflict(new { detail = "El cliente tiene pendientes registrados: elimínelos o muévalos primero." }),
        };
    }

    // ---------- Helpers ----------

    /// <summary>
    /// Resuelve el área, verifica que el módulo esté disponible y que el perfil tenga el permiso.
    /// Área desconocida es 400 (no 403): no es un problema de permisos.
    /// </summary>
    private async Task<(string? Area, IActionResult? Error)> GuardAsync(
        string area, bool edit, CancellationToken ct)
    {
        if (!factory.IsConfigured)
            return (null, StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { detail = "El tablero de pendientes no está disponible en este entorno" }));

        var resolved = PendientesArea.Resolve(area);
        if (resolved is null)
            return (null, BadRequest(new { detail = "Área inválida: use CDC o INFRA" }));

        var role = User.FindFirst(ClaimTypes.Role)?.Value ?? "";
        if (!await permissions.HasAccessAsync(role, resolved.Value.ModuleKey, edit, ct))
            return (null, StatusCode(StatusCodes.Status403Forbidden,
                new { detail = "Módulo no permitido para su perfil" }));

        return (resolved.Value.Area, null);
    }

    /// <summary>
    /// Valida el body de un pendiente. Distingue "no vino" (usa default) de "vino mal" (400), para no
    /// escribir en la BD un cuarto estado que ningún filtro reconoce.
    /// </summary>
    private static (PendienteWrite? Data, string? Error) Validate(PendienteWrite body, bool requireToken)
    {
        if (body.ClienteNum <= 0)
            return (null, "Seleccione el cliente");

        if (string.IsNullOrWhiteSpace(body.Descripcion) && string.IsNullOrWhiteSpace(body.Titulo))
            return (null, "Escriba la descripción del pendiente");

        if (requireToken && body.Actualizado is null)
            return (null, "Falta el token de concurrencia (actualizado)");

        var tipo = Pick(body.Tipo, PendientesDomain.Tipos, PendientesDomain.TipoDefault);
        if (tipo is null) return (null, "Tipo inválido: use PENDIENTE o BLOQUEANTE");

        var prioridad = Pick(body.Prioridad, PendientesDomain.Prioridades, PendientesDomain.PrioridadDefault);
        if (prioridad is null) return (null, "Prioridad inválida: use ALTA, MEDIA o BAJA");

        var estado = Pick(body.Estado, PendientesDomain.Estados, PendientesDomain.EstadoDefault);
        if (estado is null) return (null, "Estado inválido: use ABIERTO, EN_PROGRESO o CERRADO");

        return (body with { Tipo = tipo, Prioridad = prioridad, Estado = estado }, null);
    }

    private static (ClienteWrite? Data, string? Error) ValidateCliente(ClienteWrite body)
    {
        if (string.IsNullOrWhiteSpace(body.Cliente))
            return (null, "El nombre del cliente es obligatorio");

        string? categoria = null;
        if (!string.IsNullOrWhiteSpace(body.Categoria))
        {
            categoria = PendientesDomain.Normalize(body.Categoria, PendientesDomain.Categorias);
            if (categoria is null) return (null, "Categoría inválida: use ALTO, MEDIO o BAJO");
        }

        return (body with { Categoria = categoria }, null);
    }

    /// <summary>Vacío → default; con valor → normalizado o null si no está en la lista blanca.</summary>
    private static string? Pick(string? raw, string[] allowed, string fallback) =>
        string.IsNullOrWhiteSpace(raw) ? fallback : PendientesDomain.Normalize(raw, allowed);
}
