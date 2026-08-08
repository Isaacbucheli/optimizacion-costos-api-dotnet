using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;
using OptimizacionCostos.Api.Features.Storage;

namespace OptimizacionCostos.Api.Features.InformeValor.Api;

/// <summary>
/// Informe de valor del servicio administrado: carga de los insumos que no se pueden obtener
/// desde la credencial del cliente (BITCOST y la mesa de servicio, más el RBAC de respaldo).
/// El cálculo y la generación son de las entregas 2 y 3.
///
/// Subir() a propósito NO recibe IFormFile en la firma, y además lleva
/// [DisableFormValueModelBinding]. Un IFormFile como parámetro no alcanzaría solo con quitarlo:
/// el composite value provider de la acción se construye una única vez para TODOS los
/// parámetros, invocando a todos los IValueProviderFactory registrados —FormValueProviderFactory
/// incluido— y ese factory llama a Request.ReadFormAsync() en cuanto el content-type es
/// multipart/form-data, sin importar si algún parámetro en particular necesita el form. Como
/// clientId/kind bindean por ruta, esa construcción compartida ocurre igual y dispara la lectura
/// completa del cuerpo ANTES de que el método arranque, con el mismo resultado que el brief
/// original quería evitar (un archivo sobre el tope revienta durante el binding, nunca llega al
/// guard de acceso ni al chequeo de Content-Length, y el middleware de última instancia de
/// Program.cs convierte la excepción en un 500 opaco). [DisableFormValueModelBinding] saca a
/// FormValueProviderFactory (y sus primos de archivos/jQuery) de esa construcción, así que la
/// primera vez que el cuerpo se toca de verdad es el propio Request.ReadFormAsync() de este
/// método, después de que el guard de acceso y el chequeo de tamaño ya corrieron.
/// </summary>
[ApiController]
[Authorize]
[Route("informe-valor")]
[RequireModule(Modules.InformeValor)]
public sealed class InformeValorController(
    IInformeValorStore store, IAnalysisAccess access, ILogger<InformeValorController> logger,
    IInsumosBdRecolector recolector) : ControllerBase
{
    // Un export de BITCOST de 24 meses de un cliente grande está entre 8 y 18 MB, así que el
    // tope compartido de UploadValidation (10 MiB) rechazaría un archivo legítimo.
    internal const long MaxBytes = 32L * 1024 * 1024;

    // Techo que ve Kestrel/el host para esta acción. Tiene que quedar POR ENCIMA de MaxBytes:
    // el default de Kestrel (30 MiB) es menor que el tope del módulo (32 MiB), así que sin subirlo
    // un archivo legítimo de ~31-32 MiB reventaría contra el límite del framework al leer el form,
    // antes de que el chequeo de abajo tenga oportunidad de responder con un 413 prolijo. El 413
    // real lo produce siempre el código de este controller, nunca el límite del host: este techo
    // solo existe para que el host no corte primero.
    private const long RequestSizeLimitBytes = MaxBytes + 8 * 1024 * 1024;

    private static readonly HashSet<string> Kinds =
        new(StringComparer.OrdinalIgnoreCase)
        { SqlInformeValorStore.KindFacturacion, SqlInformeValorStore.KindCasos, SqlInformeValorStore.KindRbac };

    [HttpGet("clients/{clientId:int}/estado")]
    public async Task<IActionResult> Estado(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var cargados = await store.GetEstadoAsync(clientId, ct);
        var porKind = cargados.ToDictionary(x => x.Kind, StringComparer.OrdinalIgnoreCase);

        return Ok(new
        {
            insumos = new[]
            {
                Describe(SqlInformeValorStore.KindFacturacion, true, porKind),
                Describe(SqlInformeValorStore.KindCasos, true, porKind),
                Describe(SqlInformeValorStore.KindRbac, false, porKind),
            },
        });
    }

    /// <summary>
    /// Diagnóstico de los insumos que salen de la base (Advisor, Matriz, RBAC, Retiros): conteos y
    /// metadatos, nunca las filas. Le dice al consultor si el cliente tiene datos suficientes antes
    /// de generar el informe, y le dice a quien depure de dónde salió cada cifra — no es andamiaje
    /// descartable, aunque esta entrega todavía no calcula nada con lo que devuelve.
    ///
    /// Solo lee: hereda [RequireModule(Modules.InformeValor)] de la clase (ModuleAccess.View), sin
    /// la variante Edit que sí llevan Subir/Borrar.
    ///
    /// <b>No expone nombres de recurso, de suscripción ni de identidad.</b> Los datos completos
    /// salen recién en el informe (entrega 2b), que tiene su propio gating. Por eso "excluidos"
    /// existe SOLO dentro de <c>matriz</c>: is_excluded vive en la canónica de la matriz WAF y es
    /// el flag que el consultor cura desde esa pantalla (ver MatrizFila.Excluida). El bloque
    /// <c>advisor</c>, al grano recomendación × recurso, no repite ese eje — ni siquiera en cero —
    /// para que nadie confunda "este bloque no lo mide" con "no hay excluidos en Advisor".
    ///
    /// <c>seguridad_gestionada</c> existe por el mismo motivo (un cero no puede leerse como dos
    /// cosas distintas): sin ella, un cliente que gestiona su seguridad por fuera (Gestión de
    /// Vulnerabilidades) muestra <c>advisor.total</c>/<c>matriz.total</c> en cero para el pilar de
    /// Seguridad exactamente igual que un cliente sin ningún hallazgo — <see cref="MatrizRecolector"/>/
    /// <see cref="AdvisorRecolector"/> ya excluyen ese pilar cuando la bandera está prendida. Sin la
    /// marca, quien depure de dónde salió la cifra va a sospechar de la sincronización en vez de leer
    /// la decisión real del cliente. Bandera y nota vienen de <see cref="InsumosBd"/>
    /// (<see cref="InsumosBd.SeguridadGestionadaExternamente"/>/<see cref="InsumosBd.SeguridadGestionadaNota"/>),
    /// no se recalculan acá.
    /// </summary>
    [HttpGet("clients/{clientId:int}/insumos-bd")]
    public async Task<IActionResult> InsumosBd(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var insumos = await recolector.LeerAsync(clientId, ct);

        return Ok(new
        {
            advisor = new
            {
                total = insumos.Advisor.Count,
                suscripciones = insumos.Advisor
                    .Select(a => a.SubscriptionId ?? a.SubscriptionName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                con_ahorro = insumos.Advisor.Count(a => a.AhorroAnual is not null),
            },
            matriz = new
            {
                total = insumos.Matriz.Count,
                excluidos = insumos.Matriz.Count(m => m.Excluida),
            },
            rbac = new { asignaciones = insumos.Rbac.Count },
            retiros = new { total = insumos.Retiros.Count },
            seguridad_gestionada = new
            {
                gestionada_externamente = insumos.SeguridadGestionadaExternamente,
                nota = insumos.SeguridadGestionadaNota,
            },
            estado_rbac = new
            {
                disponibilidad = Disponibilidad(insumos.EstadoRbac.Disponibilidad),
                estado_cuenta_medido = insumos.EstadoRbac.Ejes.EstadoCuentaMedido,
                ultimo_login_medido = insumos.EstadoRbac.Ejes.UltimoLoginMedido,
                fecha_corrida = insumos.EstadoRbac.FechaCorrida,
                motivo = insumos.EstadoRbac.Motivo,
            },
            leido_en = insumos.LeidoEn,
        });
    }

    [HttpPost("clients/{clientId:int}/insumos/{kind}")]
    [RequireModule(Modules.InformeValor, ModuleAccess.Edit)]
    [RequestSizeLimit(RequestSizeLimitBytes)]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Subir(int clientId, string kind, CancellationToken ct)
    {
        // 1) El guard de acceso va primero: si va después de validar la extensión, un usuario sin
        // permiso recibe distinto error según cómo se llame su archivo (fuga de información).
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        // 2) El tope se mira sobre el Content-Length declarado, sin tocar el cuerpo todavía.
        if (Request.ContentLength is > MaxBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { detail = $"El archivo supera el límite permitido de {MaxBytes / (1024 * 1024)} MB." });

        if (!Kinds.Contains(kind)) return BadRequest(new { detail = "Tipo de insumo desconocido." });

        // 3) Solo ahora se lee el form (primera vez que se toca el cuerpo gracias a
        // [DisableFormValueModelBinding]). Sin Content-Length (chunked) el chequeo de arriba no
        // lo pudo anticipar; si el body real supera RequestSizeLimitBytes esto lanza, y se
        // traduce a 413 igual, nunca al 500 genérico del middleware de última instancia. Un
        // content-type que no sea multipart/form-data (ej. un cliente que manda JSON por error)
        // hace que ReadFormAsync lance InvalidOperationException ("Incorrect Content-Type"): es
        // la misma familia de defecto (excepción de framework sin capturar => 500 opaco), solo
        // que la dispara el content-type en vez del tamaño, así que se traduce a 400 acá mismo.
        IFormFile? file;
        try
        {
            var form = await Request.ReadFormAsync(ct);
            file = form.Files["file"];
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { detail = $"El archivo supera el límite permitido de {MaxBytes / (1024 * 1024)} MB." });
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { detail = "El cuerpo de la solicitud debe ser multipart/form-data." });
        }

        if (file is null || file.Length == 0) return BadRequest(new { detail = "No se recibió ningún archivo." });
        if (!ExtensionValida(file.FileName)) return BadRequest(new { detail = "El archivo debe ser un Excel (.xlsx)." });

        byte[] content;
        try
        {
            var name = UploadValidation.SafeUploadFilename(file.FileName);
            await using var input = file.OpenReadStream();
            content = await UploadValidation.ReadLimitedUploadAsync(input, MaxBytes, ct);
            if (content.Length == 0) return BadRequest(new { detail = "El archivo llegó vacío." });

            using var ms = new MemoryStream(content, writable: false);
            var user = User.FindFirst("sub")?.Value;

            if (string.Equals(kind, SqlInformeValorStore.KindFacturacion, StringComparison.OrdinalIgnoreCase))
            {
                var parsed = BitcostParser.Parse(ms);
                var id = await store.ReplaceFacturacionAsync(clientId, name, user, parsed, ct);
                return Ok(Resumen(id, parsed.RowsTotal, parsed.Rows.Count, parsed.RowsSkipped, parsed.Warnings));
            }

            if (string.Equals(kind, SqlInformeValorStore.KindCasos, StringComparison.OrdinalIgnoreCase))
            {
                var parsed = CasosParser.Parse(ms);
                var id = await store.ReplaceCasosAsync(clientId, name, user, parsed, ct);
                return Ok(Resumen(id, parsed.RowsTotal, parsed.Rows.Count, parsed.RowsSkipped, parsed.Warnings));
            }

            return BadRequest(new { detail = "La carga del insumo de RBAC llega en la entrega 2." });
        }
        catch (UploadValidationException ex) { return StatusCode(ex.StatusCode, new { detail = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { detail = ex.Message }); }
        catch (Exception ex)
        {
            logger.LogError(ex, "informe-valor subir falló client_id={Cid} kind={Kind}", clientId, kind);
            return Problem(statusCode: 500, detail: $"La carga no pudo completarse: {ex.GetType().Name}");
        }
    }

    [HttpDelete("clients/{clientId:int}/insumos/{kind}")]
    [RequireModule(Modules.InformeValor, ModuleAccess.Edit)]
    public async Task<IActionResult> Borrar(int clientId, string kind, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        if (!Kinds.Contains(kind)) return BadRequest(new { detail = "Tipo de insumo desconocido." });

        await store.DeleteInsumoAsync(clientId, kind.ToLowerInvariant(), ct);
        return NoContent();
    }

    /// <summary>snake_case en minúsculas, como el resto de los campos "status" del repo (ok/error/
    /// vigente/...), en vez del PascalCase crudo del identificador de C#.</summary>
    private static string Disponibilidad(DisponibilidadRbac d) => d switch
    {
        DisponibilidadRbac.Completo => "completo",
        DisponibilidadRbac.ParcialFaltaIdentidad => "parcial_falta_identidad",
        DisponibilidadRbac.NoDisponible => "no_disponible",
        _ => d.ToString(),
    };

    private static bool ExtensionValida(string? fileName) =>
        fileName is not null && fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

    private static object Resumen(int id, int total, int procesadas, int descartadas, IReadOnlyList<string> warnings) =>
        new
        {
            ingesta_id = id, rows_total = total, rows_processed = procesadas,
            rows_skipped = descartadas, warnings,
        };

    private static object Describe(string kind, bool obligatorio, IReadOnlyDictionary<string, InsumoEstado> cargados)
    {
        cargados.TryGetValue(kind, out var e);
        return new
        {
            kind,
            obligatorio,
            cargado = e is not null,
            source_file_name = e?.SourceFileName,
            cargado_en = e?.CargadoEn,
            filas = e?.Filas ?? 0,
            status = e?.Status,
            warnings = e?.Warnings ?? [],
        };
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden,
            new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
