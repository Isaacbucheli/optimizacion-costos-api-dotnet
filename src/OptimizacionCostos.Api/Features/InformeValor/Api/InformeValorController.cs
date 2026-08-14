using System.Globalization;
using System.Text.Json;
using Azure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.Cdc;
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Entrega;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;
using OptimizacionCostos.Api.Features.Storage;

namespace OptimizacionCostos.Api.Features.InformeValor.Api;

/// <summary>
/// Informe de valor del servicio administrado: carga de los insumos que no se pueden obtener
/// desde la credencial del cliente (BITCOST y la mesa de servicio, más el RBAC de respaldo), y
/// <see cref="Preview"/> (Tarea 8 de la entrega 2b), que calcula el modelo completo sin persistir
/// nada, más <see cref="Generar"/>/<see cref="Entregas"/>/<see cref="DescargarEntrega"/> (Tarea 4 de
/// la entrega 3): la generación del artefacto HTML, su subida a Blob y la bitácora de entregas.
///
/// La vista previa se sirve en DOS fases: <see cref="Preview"/> devuelve el informe con lo que sale
/// de la base propia y del insumo BITCOST, y <see cref="VariacionConsumo"/> devuelve aparte el
/// bloque que necesita leer las reservas del cliente en vivo contra Azure, que es la parte cara.
/// Ver el comentario de cada uno.
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
    IInsumosBdRecolector recolector, IClientStore clientStore,
    IReservationService reservations, IAzureReservationsClient reservationsClient,
    IBlobStorageService blobs, AppConfig config) : ControllerBase
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
        {
            SqlInformeValorStore.KindFacturacion, SqlInformeValorStore.KindEvolucion,
            SqlInformeValorStore.KindCasos, SqlInformeValorStore.KindRbac,
        };

    /// <summary>
    /// Qué insumos hay cargados y de cuándo, el estado de la condicional de RBAC con su motivo, y
    /// (implícito en <c>insumos</c>) qué falta para poder generar el informe. Es la pantalla de
    /// insumos la que llama esto -- al entrar y después de cada subida o borrado -- así que tiene
    /// que quedar liviano: <c>estado_rbac</c> se resuelve con
    /// <see cref="IInsumosBdRecolector.LeerEstadoRbacConOrigenAsync"/>, el camino que NO paga
    /// Advisor/Matriz/Retiros ni el schema-ensure de WAF/Boletín (a diferencia de
    /// <see cref="InsumosBd"/>, que sí los paga porque es el endpoint de diagnóstico).
    ///
    /// <para><c>estado_rbac</c> tiene la MISMA forma que el bloque homónimo de <c>/insumos-bd</c>
    /// (mismos campos, mismos nombres -- <see cref="EstadoRbacBlock"/> construye los dos): el
    /// front consume un solo tipo para los dos endpoints.</para>
    /// </summary>
    [HttpGet("clients/{clientId:int}/estado")]
    public async Task<IActionResult> Estado(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var cargados = await store.GetEstadoAsync(clientId, ct);
        var porKind = cargados.ToDictionary(x => x.Kind, StringComparer.OrdinalIgnoreCase);
        var (estadoRbac, origenRbac) = await recolector.LeerEstadoRbacConOrigenAsync(clientId, ct);

        return Ok(new
        {
            insumos = new[]
            {
                Describe(SqlInformeValorStore.KindFacturacion, true, porKind),
                Describe(SqlInformeValorStore.KindEvolucion, true, porKind),
                Describe(SqlInformeValorStore.KindCasos, true, porKind),
                Describe(SqlInformeValorStore.KindRbac, false, porKind),
            },
            estado_rbac = EstadoRbacBlock(estadoRbac, origenRbac),
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
            estado_rbac = EstadoRbacBlock(insumos.EstadoRbac, insumos.RbacOrigen),
            leido_en = insumos.LeidoEn,
        });
    }

    /// <summary>
    /// Calcula el modelo completo del informe y lo devuelve sin persistir nada (Tarea 8 de la
    /// entrega 2b: "el endpoint que devuelve el modelo"). Solo lee: hereda
    /// <c>[RequireModule(Modules.InformeValor)]</c> de la clase (<c>ModuleAccess.View</c>), igual
    /// que <see cref="Estado"/>/<see cref="InsumosBd"/> — spec, tabla de la API, <c>/preview</c> es
    /// <c>View</c> aunque sea POST, porque no muta nada.
    ///
    /// <b>Serialización: <c>Ok(modelo)</c> normal, con la política global de <c>Program.cs</c>
    /// (snake_case), nunca <see cref="InformeValorJsonOptions"/>.</b> Decisión explícita de la
    /// Tarea 8, no un descuido: son dos consumidores con dos dueños distintos.
    /// <see cref="InformeValorJsonOptions"/> existe para el HTML exportado de la entrega 3, que
    /// reusa <c>render()</c> tal cual está y no se puede tocar; este endpoint alimenta la vista
    /// React nueva de esa misma entrega, que sí se escribe desde cero y puede seguir la convención
    /// del resto de la API. Ver el comentario de clase de <see cref="InformeValorJsonOptions"/>
    /// para la duda que esto resuelve.
    ///
    /// <para>La fecha de corte llega como instante (<see cref="PreviewRequest.Corte"/>) y se
    /// resuelve a fecha de Guayaquil UNA vez, acá — nunca <c>DateTime.Now</c>/<c>UtcNow</c> (Global
    /// Constraints): dos llamadas con el mismo <see cref="PreviewRequest"/> tienen que devolver
    /// exactamente el mismo modelo.</para>
    ///
    /// <para><b>Este endpoint NO lee reservas de Azure.</b> Todo lo que pide sale de la base propia
    /// o del insumo BITCOST, y esa es la razón por la que responde rápido. La foto de reservas de la
    /// entrega 2d cuesta una llamada a Consumption por cada reserva activa, en secuencia (ver el
    /// comentario de clase de <see cref="ReservasRecolector"/>), así que vive en su propia llamada:
    /// <see cref="VariacionConsumo"/>. Acá el eje sale declarado "no medido, se pide aparte" y el
    /// consumidor completa el bloque con esa segunda llamada — la misma carga en dos fases que ya
    /// usa la pantalla de reservas del producto.</para>
    /// </summary>
    [HttpPost("clients/{clientId:int}/preview")]
    public async Task<IActionResult> Preview(int clientId, [FromBody] PreviewRequest request, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        if (ContextoDe(request) is not { } contexto) return BadRequest(new { detail = RangoInvalido });

        var insumosBd = await recolector.LeerAsync(clientId, ct);
        var facturacion = await store.GetFacturacionAsync(clientId, ct);
        var casos = await store.GetCasosAsync(clientId, ct);
        var estados = await store.GetEstadoAsync(clientId, ct);
        var nombreCliente = await clientStore.GetNameAsync(clientId, ct) ?? $"Cliente {clientId}";

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, FilasAntesDeFusionar(estados), casos, insumosBd, nombreCliente, contexto);

        return Ok(modelo);
    }

    /// <summary>
    /// D14: rows_processed + rows_merged de la ÚLTIMA carga de facturación (<c>GetEstadoAsync</c> ya
    /// filtra a la más reciente por kind). 0 si nunca se cargó nada, igual que un insumo ausente:
    /// <c>ConsumoCalculador.Calcular</c> devuelve null si además no hay ninguna fila en rango.
    ///
    /// <para>Lo comparten <see cref="Preview"/> y <see cref="Generar"/>: es el N de "revisado línea
    /// por línea sobre N registros" y tiene que salir igual en la vista previa que en el informe
    /// entregado. Dos cálculos separados del mismo conteo, cada uno coherente consigo mismo, es el
    /// defecto que este módulo arrastra.</para>
    /// </summary>
    private static int FilasAntesDeFusionar(IReadOnlyList<InsumoEstado> estados) =>
        EstadoDe(estados, SqlInformeValorStore.KindFacturacion) is { } e ? e.Filas + e.RowsMerged : 0;

    private static InsumoEstado? EstadoDe(IReadOnlyList<InsumoEstado> estados, string kind) =>
        estados.FirstOrDefault(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fase 2 de <see cref="Preview"/>: el bloque <c>fact.variacionConsumo</c> con las reservas del
    /// cliente leídas en vivo contra Azure (<see cref="FotoReservas"/>, Tarea 1 de la entrega 2d).
    /// Mismo cuerpo que <see cref="Preview"/> —el mismo período y el mismo corte, o el bloque mediría
    /// otra ventana— y solo lectura, así que hereda <c>[RequireModule(Modules.InformeValor)]</c>
    /// (<c>ModuleAccess.View</c>) de la clase y hace la misma verificación de acceso por cliente que
    /// el resto, igual que <see cref="Preview"/>.
    ///
    /// <para><b>Por qué existe.</b> Leer reservas cuesta una llamada a Consumption por cada reserva
    /// activa, en secuencia, y es la consulta más pesada de ese servicio (detalle diario de 30 días).
    /// Con eso adentro, <see cref="Preview"/> —que es lo primero que ve el consultor— pasaba a tardar
    /// decenas de segundos en un cliente con varias reservas, sobre un App Service B1 de 1 core. La
    /// pantalla de reservas del producto ya resuelve este mismo problema partiendo la carga en dos
    /// fases (primero la lista, después la utilización), y acá se hace lo mismo.</para>
    ///
    /// <para><b>Devuelve el bloque completo</b> (<see cref="VariacionConsumoModelo"/>: los tres
    /// baldes más la variación total), no solo el eje de reservas, porque el balde de reservas le
    /// saca recursos a los otros dos (E3/E9) — ver <see cref="InformeValorEnsamblador.EnsamblarVariacionConsumo"/>.
    /// El consumidor reemplaza <c>fact.variacionConsumo</c> entero con lo que vuelve de acá.</para>
    ///
    /// <para><b>La foto que se ARCHIVA no es esta.</b> E7 pide que la foto que vale sea la del
    /// momento de generar el informe, para que un informe reemitido no cambie de cifras; eso es de la
    /// entrega 3 (<c>/generar</c>, que la persiste junto a la entrega). Esta es una vista previa en
    /// vivo: dos llamadas seguidas pueden diferir si Azure cambió en el medio.</para>
    ///
    /// <para><b>De la base pide solo lo que este bloque usa</b>
    /// (<see cref="IInsumosBdRecolector.LeerHallazgosResueltosAsync"/>, no <c>LeerAsync</c>): el único
    /// insumo de base que la variación del consumo lee son los hallazgos resueltos, y el recolector
    /// completo —Advisor, Matriz, Retiros y la corrida de Revisión de accesos con su snapshot— ya lo
    /// pagó <see cref="Preview"/> hace un segundo, en la primera fase de la MISMA vista previa.</para>
    /// </summary>
    [HttpPost("clients/{clientId:int}/preview/variacion-consumo")]
    public async Task<IActionResult> VariacionConsumo(
        int clientId, [FromBody] PreviewRequest request, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        if (ContextoDe(request) is not { } contexto) return BadRequest(new { detail = RangoInvalido });

        // La lectura de Azure (decenas de segundos con varias reservas) arranca ANTES de las dos
        // consultas a SQL y se espera al final: son independientes entre sí, así que la latencia del
        // endpoint es la mayor de las dos y no la suma. Es seguro dejarla en vuelo mientras corren
        // los await de abajo porque CapturarFotoReservasAsync no propaga ninguna excepción (traduce
        // todo a una foto "no medida"): si una de esas dos consultas falla, esta tarea queda
        // abandonada pero nunca con una excepción sin observar.
        var fotoPendiente = CapturarFotoReservasAsync(clientId, ct);
        var hallazgosResueltos = await recolector.LeerHallazgosResueltosAsync(clientId, ct);
        var facturacion = await store.GetFacturacionAsync(clientId, ct);

        var variacion = InformeValorEnsamblador.EnsamblarVariacionConsumo(
            facturacion, hallazgosResueltos, contexto, await fotoPendiente);

        return Ok(variacion);
    }

    private const string RangoInvalido = "El rango del periodo es invalido: el fin es anterior al inicio.";

    /// <summary>El contexto de cálculo del cuerpo, o <c>null</c> si el rango está invertido. Lo
    /// comparten <see cref="Preview"/> y <see cref="VariacionConsumo"/>: las dos fases tienen que
    /// resolver el corte igual (<see cref="Fechas.ResolverFechaEnGuayaquil"/>, el único punto de
    /// conversión de zona horaria del módulo) y rechazar el mismo cuerpo, o devolverían bloques
    /// medidos sobre ventanas distintas.</summary>
    private static ContextoInformeValor? ContextoDe(PreviewRequest request) =>
        request.PeriodEnd < request.PeriodStart
            ? null
            : new ContextoInformeValor(
                request.PeriodStart, request.PeriodEnd,
                Fechas.ResolverFechaEnGuayaquil(request.Corte),
                request.MesesParcialesForzados);

    /// <summary>
    /// Captura la <see cref="FotoReservas"/> (Tarea 1 de la entrega 2d) para
    /// <see cref="VariacionConsumo"/>. La lectura es en vivo contra Azure -- por credencial activa
    /// del cliente, más una llamada a Consumption por cada reserva activa (ver el comentario de clase
    /// de <see cref="ReservasRecolector"/>) -- así que puede fallar por motivos ajenos al informe
    /// (SQL al listar credenciales, Azure caído, throttling). Los otros dos baldes del bloque no
    /// dependen de este eje, así que una falla acá NUNCA puede tumbar la respuesta completa.
    ///
    /// <para><see cref="ReservasRecolector.CapturarAsync"/> ya atrapa los fallos de Azure que puede
    /// anticipar (sin credenciales activas, <c>FetchAllAsync</c> completo, <c>GetConsumersAsync</c>
    /// puntual de una reserva) y los traduce a <see cref="FotoReservas"/> con <c>Medido=false</c>.
    /// Lo que NO atrapa es <c>IReservationService.ActiveCredentialsAsync</c> (una consulta SQL): ese
    /// hueco, y cualquier otra falla que no se haya anticipado, se cierran acá -- con el MISMO
    /// contrato (<c>Medido=false</c> + <c>Motivo</c> + <c>Errores</c>) que ya usa el recolector, sin
    /// inventar un estado nuevo.</para>
    /// </summary>
    private async Task<FotoReservas> CapturarFotoReservasAsync(int clientId, CancellationToken ct)
    {
        try
        {
            return await ReservasRecolector.CapturarAsync(reservations, reservationsClient, clientId, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "informe-valor variacion-consumo: la lectura de reservas fallo, el eje se publica no medido. client_id={Cid}",
                clientId);
            return new FotoReservas(
                Medido: false,
                Motivo: "La lectura de reservas contra Azure fallo de forma inesperada: este eje no se midio.",
                Errores: [new { error = ex.GetType().Name }],
                AlertDays: ReservasRecolector.AlertDaysPorDefecto,
                CapturadaEn: DateTime.UtcNow,
                Reservas: []);
        }
    }

    // ===================================================================================
    // Entrega (Tarea 4 de la entrega 3): generar, listar y descargar.
    // ===================================================================================

    private const string ContentTypeHtml = "text/html; charset=utf-8";

    /// <summary>
    /// Calcula el informe, arma el artefacto HTML, lo sube a Blob y archiva la entrega. Es la única
    /// escritura de este flujo, así que lleva <c>ModuleAccess.Edit</c>; <see cref="Entregas"/> y
    /// <see cref="DescargarEntrega"/> son <c>View</c> (spec, tabla de la API).
    ///
    /// <para><b>Acá SÍ se captura la foto de reservas</b> (E7 de la entrega 2d, decisión del
    /// usuario), y es el único lugar donde se captura para archivar: la foto que vale es la del
    /// momento de generar, para que un informe reemitido meses después muestre lo que era cierto
    /// entonces y no lo que Azure devuelve hoy. Es una acción deliberada del consultor, así que unos
    /// segundos de espera son aceptables — a diferencia de <see cref="Preview"/>, de donde la lectura
    /// se sacó a propósito porque es lo primero que se pinta en pantalla. Se captura para las DOS
    /// variantes, aunque la del cliente no publique el bloque: la fila archivada tiene que quedar
    /// completa igual, o reemitirla como interna volvería a leer las reservas de hoy.</para>
    ///
    /// <para><b>El orden de lo que se persiste importa:</b> primero el blob, después la fila. Al
    /// revés, un fallo de Storage dejaría una entrega archivada que apunta a un artefacto que no
    /// existe, y la descarga fallaría más adelante sin explicación. Así, un fallo de Storage no
    /// archiva nada y el consultor puede volver a intentar; lo que queda suelto es a lo sumo un blob
    /// que nadie referencia, y eso se dice en el log.</para>
    ///
    /// <para><b>Lo que se archiva es lo que el artefacto HACE, no lo que se pidió</b>
    /// (<see cref="ArtefactoInforme.BloquesPublicados"/>): pedir la variante interna con tres bloques
    /// aprobados produce el informe completo, y la bitácora dice los ocho. La respuesta devuelve el
    /// mismo dato para que quien llama vea qué pasó de verdad.</para>
    /// </summary>
    [HttpPost("clients/{clientId:int}/generar")]
    [RequireModule(Modules.InformeValor, ModuleAccess.Edit)]
    public async Task<IActionResult> Generar(
        int clientId, [FromBody] GenerarRequest request, CancellationToken ct)
    {
        // El guard de acceso va primero, igual que en Subir: nada del cuerpo se valida —ni se lee
        // Azure— para un cliente al que quien llama no tiene acceso.
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        // La variante es obligatoria y no tiene default seguro: asumir "interna" publicaría el
        // informe completo a un cliente, y asumir "cliente" entregaría un informe sin cifras al
        // consultor que las esperaba.
        if (VarianteInformeExtensions.Parsear(request.Variante) is not { } variante)
            return BadRequest(new
            {
                detail = "La variante del informe es obligatoria y debe ser 'interna' o 'cliente'.",
            });

        // Un bloque que no se reconoce es un 400, nunca un bloque que se ignora: el consultor creyó
        // aprobarlo y el informe saldría sin esa cifra sin que nadie se lo diga (F1).
        var bloques = new List<BloqueEconomico>();
        foreach (var clave in request.Bloques ?? [])
        {
            if (BloqueEconomicoExtensions.Parsear(clave) is not { } bloque)
                return BadRequest(new
                {
                    detail = $"Bloque economico desconocido: '{clave}'. Los validos son: " +
                        string.Join(", ", BloqueEconomicoExtensions.Todos.Select(b => b.Clave())) + ".",
                });
            bloques.Add(bloque);
        }

        if (ContextoDe(request.Periodo) is not { } contexto) return BadRequest(new { detail = RangoInvalido });

        // La lectura de Azure (decenas de segundos con varias reservas) arranca antes de las
        // consultas a SQL y se espera al final, igual que en VariacionConsumo y por el mismo motivo:
        // son independientes, y CapturarFotoReservasAsync no propaga ninguna excepción, así que
        // dejarla en vuelo no puede terminar en una tarea con excepción sin observar.
        var fotoPendiente = CapturarFotoReservasAsync(clientId, ct);

        var insumosBd = await recolector.LeerAsync(clientId, ct);
        var facturacion = await store.GetFacturacionAsync(clientId, ct);
        var casos = await store.GetCasosAsync(clientId, ct);
        var estados = await store.GetEstadoAsync(clientId, ct);
        var nombreCliente = await clientStore.GetNameAsync(clientId, ct) ?? $"Cliente {clientId}";
        var foto = await fotoPendiente;

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, FilasAntesDeFusionar(estados), casos, insumosBd, nombreCliente, contexto, foto);

        ArtefactoInforme artefacto;
        try
        {
            artefacto = InformeValorHtmlExporter.Exportar(modelo, variante, bloques);
        }
        catch (InvalidOperationException ex)
        {
            // La plantilla embebida perdió uno de sus marcadores. El exportador falla a propósito en
            // vez de entregar un artefacto a medias (por ejemplo con la zona de carga adentro), así
            // que acá se traduce a un 500 que dice qué revisar, no a un 500 opaco.
            logger.LogError(ex, "informe-valor generar: la plantilla embebida no se pudo instanciar client_id={Cid}", clientId);
            return Problem(statusCode: 500,
                detail: "La plantilla del informe no se pudo instanciar: no se generó ningún archivo.");
        }

        var container = config.StorageContainerOutputs;
        var blobName = NombreDeBlob(clientId, contexto, variante);
        try
        {
            await blobs.UploadAsync(container, blobName, artefacto.Contenido, ContentTypeHtml, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "informe-valor generar: no se pudo subir el artefacto client_id={Cid} blob={Blob}", clientId, blobName);
            return Problem(statusCode: 500,
                detail: "El informe se calculó pero no se pudo guardar en el almacenamiento: no se archivó ninguna entrega.");
        }

        int entregaId;
        try
        {
            entregaId = await store.RegistrarEntregaAsync(
                new EntregaNueva(
                    ClientId: clientId,
                    PeriodStart: contexto.PeriodStart,
                    PeriodEnd: contexto.PeriodEnd,
                    Corte: contexto.Corte,
                    // El tri-estado tal cual llegó, sin normalizar: null (heurística automática) y
                    // lista vacía ("ningún mes parcial") reemiten distinto.
                    MesesParcialesForzados: contexto.MesesParcialesForzados,
                    Variante: variante,
                    BloquesPublicados: artefacto.BloquesPublicados,
                    RbacOrigen: insumosBd.RbacOrigen,
                    RbacCorridaFecha: insumosBd.EstadoRbac.FechaCorrida,
                    SeguridadGestionadaExternamente: insumosBd.SeguridadGestionadaExternamente,
                    FacturacionIngestaId: EstadoDe(estados, SqlInformeValorStore.KindFacturacion)?.IngestaId,
                    CasosIngestaId: EstadoDe(estados, SqlInformeValorStore.KindCasos)?.IngestaId,
                    RbacIngestaId: EstadoDe(estados, SqlInformeValorStore.KindRbac)?.IngestaId,
                    FotoReservas: foto,
                    PlantillaVersion: artefacto.PlantillaVersion,
                    BlobContainer: container,
                    BlobName: blobName,
                    BlobSizeBytes: artefacto.Contenido.Length,
                    FileName: artefacto.FileName,
                    SummaryJson: Resumen(modelo, foto, artefacto),
                    GeneratedBy: User.FindFirst("sub")?.Value),
                ct);
        }
        catch (Exception ex)
        {
            // El artefacto quedó subido y sin fila que lo referencie. Se dice con el nombre del blob
            // adentro: es la única forma de encontrarlo después, y callarlo dejaría un archivo con
            // datos del cliente en el contenedor sin que nadie sepa que está ahí.
            logger.LogError(ex,
                "informe-valor generar: el artefacto se subió pero la entrega no se archivó client_id={Cid} blob={Blob}",
                clientId, blobName);
            return Problem(statusCode: 500,
                detail: "El informe se generó y se guardó, pero la entrega no se pudo archivar: no quedó registrada.");
        }

        return Ok(new
        {
            entrega_id = entregaId,
            variante = variante.Clave(),
            // Lo que el artefacto publica de verdad (ver el comentario del método).
            bloques_publicados = artefacto.BloquesPublicados.Select(b => b.Clave()),
            bloques_totales = BloqueEconomicoExtensions.Todos.Count,
            file_name = artefacto.FileName,
            container,
            blob_name = blobName,
            blob_size_bytes = artefacto.Contenido.Length,
            plantilla_version = artefacto.PlantillaVersion,
            // El eje de reservas, en la respuesta y no solo dentro del archivo: si la lectura falló,
            // el balde sale en cero y quien generó el informe tiene que poder saber que ese cero es
            // "no se midió" y no "el cliente no tiene reservas".
            reservas = new
            {
                medido = foto.Medido,
                motivo = foto.Motivo,
                capturada_en = foto.CapturadaEn,
                total = foto.Reservas.Count,
            },
            download_url = DownloadUrl(clientId, entregaId),
        });
    }

    /// <summary>
    /// La bitácora de entregas de un cliente, de la más reciente a la más vieja. Solo lectura:
    /// hereda <c>[RequireModule(Modules.InformeValor)]</c> (<c>ModuleAccess.View</c>) de la clase.
    ///
    /// <para><c>bloques_totales</c> viaja al lado de <c>bloques_publicados</c> para que la tabla no
    /// tenga que inventar el denominador: "0 de 6 bloques económicos" y "0" no afirman lo mismo, y
    /// hardcodear el 6 del otro lado es exactamente cómo dos piezas empiezan a discrepar.</para>
    /// </summary>
    [HttpGet("clients/{clientId:int}/entregas")]
    public async Task<IActionResult> Entregas(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var entregas = await store.GetEntregasAsync(clientId, ct);

        return Ok(new
        {
            entregas = entregas.Select(e => new
            {
                entrega_id = e.EntregaId,
                period_start = e.PeriodStart,
                period_end = e.PeriodEnd,
                corte = e.Corte,
                variante = e.Variante,
                bloques_publicados = e.BloquesPublicados,
                bloques_totales = BloqueEconomicoExtensions.Todos.Count,
                rbac_origen = e.RbacOrigen,
                file_name = e.FileName,
                blob_size_bytes = e.BlobSizeBytes,
                generated_by = e.GeneratedBy,
                generated_at = e.GeneratedAt,
                download_url = DownloadUrl(clientId, e.EntregaId),
            }),
        });
    }

    /// <summary>
    /// Devuelve el artefacto archivado tal cual se entregó. Solo lectura (<c>ModuleAccess.View</c>
    /// heredado de la clase) y con la misma verificación de acceso por cliente que el resto; el
    /// filtro por <c>client_id</c> además va dentro del <c>WHERE</c> del store, así que un
    /// <c>entregaId</c> adivinado no devuelve el informe de otro cliente.
    ///
    /// <para><b>El contenedor sale de la FILA, no de la configuración de hoy</b>: es lo que ya hacen
    /// las dos descargas del módulo de informes (<c>ExcelController</c> lee
    /// <c>record.StorageContainer</c> y <c>ReportsController</c> lee <c>reference.Container</c>).
    /// Deducirlo de <c>STORAGE_CONTAINER_OUTPUTS</c> significa que cambiar esa variable deja sin
    /// artefacto a todo lo ya archivado, con un 404 que no se explica mirando la fila.</para>
    ///
    /// <para><b>Los tres finales son tres hechos distintos y se responden distinto</b>: la entrega no
    /// existe para este cliente (404), la entrega existe pero su artefacto ya no está en Storage
    /// (404 con otro texto), y el almacenamiento no se pudo leer (500). Colapsarlos en un solo 404
    /// convertiría una credencial vencida de Storage en "el archivo se borró".</para>
    ///
    /// <para>Se sirve como adjunto (<c>File(...)</c> con nombre) y nunca en línea: es HTML con datos
    /// del cliente adentro y no tiene por qué ejecutarse en el origen de la API.</para>
    /// </summary>
    [HttpGet("clients/{clientId:int}/entregas/{entregaId:int}/descargar")]
    public async Task<IActionResult> DescargarEntrega(int clientId, int entregaId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var entrega = await store.GetEntregaAsync(clientId, entregaId, ct);
        if (entrega is null) return NotFound(new { detail = "La entrega no existe para este cliente." });

        var container = entrega.BlobContainer;
        if (string.IsNullOrWhiteSpace(container))
        {
            // Solo puede pasar con una fila escrita antes de que la columna existiera. Se asume el
            // contenedor configurado hoy —la única suposición razonable— pero se registra: si la
            // descarga falla, quien depure necesita saber que el contenedor lo puso el entorno y no
            // la fila.
            container = config.StorageContainerOutputs;
            logger.LogWarning(
                "informe-valor descargar: la entrega no tiene contenedor archivado, se asume el configurado. entrega_id={Eid} container={Cont}",
                entregaId, container);
        }

        byte[] data;
        try
        {
            data = await blobs.DownloadAsync(container, entrega.BlobName, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            logger.LogWarning(ex,
                "informe-valor descargar: el artefacto archivado ya no está en Storage entrega_id={Eid} blob={Blob}",
                entregaId, entrega.BlobName);
            return NotFound(new
            {
                detail = "La entrega está archivada pero su artefacto ya no está en el almacenamiento: " +
                    "no se puede descargar. Hay que volver a generar el informe.",
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "informe-valor descargar: el almacenamiento no se pudo leer entrega_id={Eid} blob={Blob}",
                entregaId, entrega.BlobName);
            return Problem(statusCode: 500,
                detail: "El artefacto está archivado pero el almacenamiento no respondió: la descarga no se pudo completar.");
        }

        return File(data, ContentTypeHtml, entrega.Resumen.FileName);
    }

    private static string DownloadUrl(int clientId, int entregaId) =>
        $"/informe-valor/clients/{clientId}/entregas/{entregaId}/descargar";

    /// <summary>
    /// Ruta del artefacto dentro del contenedor de salidas. Misma forma que el informe de gestión
    /// mensual (<c>reports/client-{id}/...</c>) con una diferencia deliberada: lleva un sufijo único,
    /// porque reemitir el mismo período es legítimo (F4) y un nombre derivado solo del período haría
    /// que la segunda emisión sobrescribiera el artefacto de la primera — la fila vieja quedaría
    /// apuntando a un archivo con contenido nuevo, que es peor que no tenerla.
    ///
    /// <para><b>No lleva el nombre de descarga</b> (<see cref="ArtefactoInforme.FileName"/>): ése
    /// depende del nombre del cliente y puede ser largo, y <c>blob_name</c> se guarda en un
    /// <c>NVARCHAR(400)</c> que el store trunca. Un nombre truncado al guardarlo dejaría de coincidir
    /// con el que se subió y la descarga fallaría con un 404 inexplicable. Acá el largo es fijo.</para>
    /// </summary>
    private static string NombreDeBlob(int clientId, ContextoInformeValor contexto, VarianteInforme variante) =>
        $"informe-valor/client-{clientId}/" +
        $"{contexto.PeriodStart.ToString("yyyyMM", CultureInfo.InvariantCulture)}-" +
        $"{contexto.PeriodEnd.ToString("yyyyMM", CultureInfo.InvariantCulture)}-" +
        $"{variante.Clave()}-{Guid.NewGuid():N}.html";

    /// <summary>
    /// <c>summary_json</c> de la entrega: para qué sirve la fila sin bajar el artefacto de 200+ KB.
    ///
    /// <para><b>Sin montos, a propósito.</b> La bitácora guarda las dos variantes en la misma tabla,
    /// así que un resumen con cifras sería un camino por el que un monto suprimido en la variante del
    /// cliente podría reaparecer en cualquier pantalla que muestre el historial. Los montos viven en
    /// el artefacto, que es el único lugar donde la decisión de publicación ya está aplicada. Lo que
    /// sí va es todo lo que explica de dónde salieron: qué bloques del modelo se pudieron armar, qué
    /// meses se trataron como parciales y en qué estado quedó el eje de reservas.</para>
    ///
    /// <para>Las claves van escritas en minúsculas dentro del objeto anónimo para que no dependan de
    /// la política de nombres: se serializa con <see cref="InformeValorJsonOptions"/> (la misma que
    /// las otras dos columnas JSON de esta tabla), que no transforma nada.</para>
    /// </summary>
    private static string Resumen(ModeloInformeValor modelo, FotoReservas foto, ArtefactoInforme artefacto) =>
        JsonSerializer.Serialize(new
        {
            cliente = modelo.Meta.Cliente,
            periodo = modelo.Meta.Periodo,
            corte = modelo.Meta.Corte,
            suscripciones = modelo.Meta.Cobertura.Total,
            bloques_del_modelo = new
            {
                consumo = modelo.Consumo is not null,
                operacion = modelo.Operacion is not null,
                seguridad = modelo.Seguridad is not null,
                postura = modelo.Postura is not null,
                roadmap = modelo.Roadmap is not null,
            },
            // null cuando no hay bloque de consumo ("no se pudo determinar"), lista vacía cuando sí
            // lo hay y ningún mes resultó parcial. Son dos cosas distintas.
            meses_parciales = modelo.Consumo?.MesesParciales,
            reservas = new
            {
                medido = foto.Medido,
                motivo = foto.Motivo,
                capturada_en = foto.CapturadaEn,
                total = foto.Reservas.Count,
            },
            plantilla_version = artefacto.PlantillaVersion,
        }, InformeValorJsonOptions.Instance);

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
                return Ok(Resumen(id, parsed.RowsTotal, parsed.Rows.Count, parsed.RowsSkipped, parsed.RowsMerged, parsed.Warnings));
            }

            if (string.Equals(kind, SqlInformeValorStore.KindEvolucion, StringComparison.OrdinalIgnoreCase))
            {
                var parsed = EvolucionParser.Parse(ms);
                var id = await store.ReplaceEvolucionAsync(clientId, name, user, parsed, ct);
                return Ok(Resumen(id, parsed.RowsTotal, parsed.Rows.Count, parsed.RowsSkipped, parsed.RowsMerged, parsed.Warnings));
            }

            if (string.Equals(kind, SqlInformeValorStore.KindCasos, StringComparison.OrdinalIgnoreCase))
            {
                var parsed = CasosParser.Parse(ms);
                var id = await store.ReplaceCasosAsync(clientId, name, user, parsed, ct);
                return Ok(Resumen(id, parsed.RowsTotal, parsed.Rows.Count, parsed.RowsSkipped, parsed.RowsMerged, parsed.Warnings));
            }

            if (string.Equals(kind, SqlInformeValorStore.KindRbac, StringComparison.OrdinalIgnoreCase))
            {
                var parsed = RbacParser.Parse(ms);

                // Decisión 4 del brief ("precedencia: gana la base"): si el consultor sube el
                // archivo habiendo datos completos en la base, gana la base. El archivo se
                // descarta -- pero se avisa en la respuesta, nunca en silencio: un archivo
                // descartado sin decirlo es un consultor convencido de que subió algo que no se
                // usó. LeerEstadoRbacAsync resuelve solo esto (sin Advisor/Matriz/Retiros ni el
                // schema-ensure de WAF/Boletín): a diferencia de /preview, esta es una acción
                // manual del consultor que no necesita nada más que el recolector completo trae.
                var estadoRbac = await recolector.LeerEstadoRbacAsync(clientId, ct);
                if (estadoRbac.Disponibilidad == DisponibilidadRbac.Completo)
                    return Ok(ResumenRbacDescartado(parsed));

                var id = await store.ReplaceRbacAsync(clientId, name, user, parsed, ct);
                return Ok(ResumenRbac(id, parsed));
            }

            return BadRequest(new { detail = "Tipo de insumo desconocido." });
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

    /// <summary>
    /// Forma del bloque <c>estado_rbac</c>, compartida por <see cref="Estado"/> e
    /// <see cref="InsumosBd"/> para que las dos rutas devuelvan exactamente los mismos campos con
    /// los mismos nombres -- en particular <c>origen</c>, nunca <c>rbac_origen</c> (nombre que ya
    /// generó confusión una vez). <see cref="Estado"/> lo resuelve por el camino liviano
    /// (<see cref="IInsumosBdRecolector.LeerEstadoRbacConOrigenAsync"/>); <see cref="InsumosBd"/>
    /// lo resuelve como parte del <see cref="Recolector.InsumosBd"/> completo
    /// (<see cref="IInsumosBdRecolector.LeerAsync"/>). Un solo método construye el JSON para los
    /// dos, así que la forma no puede divergir por un campo que se agregue de un lado y no del
    /// otro.
    /// </summary>
    private static object EstadoRbacBlock(EstadoRbacResultado estado, string? origen) => new
    {
        disponibilidad = Disponibilidad(estado.Disponibilidad),
        estado_cuenta_medido = estado.Ejes.EstadoCuentaMedido,
        ultimo_login_medido = estado.Ejes.UltimoLoginMedido,
        fecha_corrida = estado.FechaCorrida,
        motivo = estado.Motivo,
        // De dónde saldrían las filas de RBAC que alimentan el informe: "base"/"archivo", o null
        // si ninguna de las dos fuentes tiene nada. Quien depure de dónde salió una cifra del
        // bloque de seguridad necesita esto, no solo la disponibilidad de la base -- los dos
        // pueden discrepar (base parcial, pero el insumo efectivo es el archivo).
        origen,
    };

    private static bool ExtensionValida(string? fileName) =>
        fileName is not null && fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// rows_merged solo es distinto de cero en facturación (BitcostParser fusiona filas con la
    /// misma clave natural; CasosParser nunca). Va junto a los otros tres para que el consultor
    /// pueda leerlos juntos sin contradicción: rows_total = rows_processed + rows_skipped +
    /// rows_merged.
    /// </summary>
    private static object Resumen(
        int id, int total, int procesadas, int descartadas, int fusionadas, IReadOnlyList<string> warnings) =>
        new
        {
            ingesta_id = id, rows_total = total, rows_processed = procesadas,
            rows_skipped = descartadas, rows_merged = fusionadas, warnings,
        };

    /// <summary>Mismo contrato de <see cref="Resumen"/> (rows_total = rows_processed +
    /// rows_skipped; rows_merged siempre 0 porque RbacParser nunca fusiona), más
    /// <c>descartado: false</c> para que el consultor no tenga que inferirlo de la presencia de
    /// <c>ingesta_id</c>.</summary>
    private static object ResumenRbac(int id, RbacParseResult parsed) => new
    {
        ingesta_id = id, rows_total = parsed.RowsTotal, rows_processed = parsed.Rows.Count,
        rows_skipped = parsed.RowsSkipped, rows_merged = 0, warnings = parsed.Warnings,
        descartado = false,
    };

    /// <summary>Decisión 4: el archivo se descartó porque la base ya tiene el insumo de RBAC
    /// completo para este cliente. Sin <c>ingesta_id</c> a propósito -- no se creó ninguna corrida,
    /// nada se persistió -- pero con las mismas cifras que <see cref="ResumenRbac"/> hubiera
    /// reportado, para que el consultor vea qué había en el archivo aunque no se haya usado.</summary>
    private static object ResumenRbacDescartado(RbacParseResult parsed) => new
    {
        descartado = true,
        detail = "La base ya tiene el insumo de RBAC completo para este cliente: se descartó el " +
            "archivo. El informe usa los datos de Revisión de accesos, no este Excel.",
        rows_total = parsed.RowsTotal, rows_processed = parsed.Rows.Count,
        rows_skipped = parsed.RowsSkipped, rows_merged = 0, warnings = parsed.Warnings,
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
            rows_merged = e?.RowsMerged ?? 0,
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

/// <summary>
/// Cuerpo de <see cref="InformeValorController.Preview"/>. El rango y el corte SIEMPRE entran
/// como parámetros (Global Constraints): nunca se completan con el reloj del servidor.
///
/// <para><see cref="Corte"/> es un instante (no una fecha ya resuelta): la resolución a fecha de
/// Guayaquil pasa por <see cref="Fechas.ResolverFechaEnGuayaquil"/> dentro del controller, el único
/// punto de conversión de zona horaria del módulo.</para>
///
/// <para><see cref="MesesParcialesForzados"/> es el tri-estado del spec §12.3.3: ausente/<c>null</c>
/// en el JSON = heurística automática; <c>[]</c> = el consultor declaró "ningún mes parcial";
/// una lista con elementos = exactamente esos meses. El binder de JSON ya distingue los tres
/// (<c>null</c> y ausente bindean igual a <c>null</c> en C#, que es la heurística: es la lectura
/// correcta, porque para quien llama "no mandé el campo" y "mandé null explícito" significan lo
/// mismo, ninguna declaración).</para>
/// </summary>
public sealed record PreviewRequest(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateTimeOffset Corte,
    IReadOnlyList<string>? MesesParcialesForzados);

/// <summary>
/// Cuerpo de <see cref="InformeValorController.Generar"/>: el mismo período que
/// <see cref="PreviewRequest"/> más las dos decisiones de entrega (la variante y los bloques
/// económicos aprobados).
///
/// <para><see cref="Periodo"/> devuelve exactamente un <see cref="PreviewRequest"/> para que las tres
/// rutas resuelvan el contexto de cálculo con la MISMA función
/// (<c>InformeValorController.ContextoDe</c>). Si <c>/generar</c> resolviera el corte o el tri-estado
/// de meses parciales por su cuenta, el informe entregado podría medir otra ventana que la vista
/// previa que el consultor aprobó, y las dos piezas serían coherentes consigo mismas.</para>
///
/// <para><see cref="Bloques"/> ausente, <c>null</c> o vacío es "ninguno aprobado": los ocho nacen
/// apagados (F1) y generar sin decidir produce la versión sin cifras, que es el default del spec y no
/// un error. <see cref="Variante"/>, en cambio, es obligatoria — ver <c>Generar</c> para por qué no
/// hay default seguro.</para>
/// </summary>
public sealed record GenerarRequest(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateTimeOffset Corte,
    IReadOnlyList<string>? MesesParcialesForzados,
    string? Variante,
    IReadOnlyList<string>? Bloques)
{
    public PreviewRequest Periodo => new(PeriodStart, PeriodEnd, Corte, MesesParcialesForzados);
}
