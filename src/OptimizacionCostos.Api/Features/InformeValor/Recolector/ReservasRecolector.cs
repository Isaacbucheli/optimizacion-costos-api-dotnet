using OptimizacionCostos.Api.Features.Cdc;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>
/// La foto de reservas de un cliente en el momento de generar el informe de valor (Tarea 1 del
/// plan de la entrega 2d, E2 y E7). Fuente: <see cref="IReservationService"/> e
/// <see cref="IAzureReservationsClient"/> (namespace <c>Cdc</c>, ya desplegados) — nunca el insumo
/// de facturacion. La correccion que origino esta tarea: inferir cobertura desde la subcategoria
/// "Reservation-Base VM" de un export de facturacion no dice que reserva cubre esas horas, ni si
/// esta confirmada, ni si el permiso para verificarlo existe. La app ya resuelve eso contra Azure,
/// con permisos reales, y esta foto es el punto donde ese hecho entra al informe.
///
/// <para><b>Es la captura, no el calculo.</b> Esta clase hace IO (llama a Azure vía los dos
/// servicios de <c>Cdc</c>): vive del lado del recolector, no de <c>Calculo</c>, para que el
/// ahorro atribuido a estas reservas (Tarea 2, <see cref="Calculo.AhorroReservasCalculador"/>) sea
/// una funcion pura que recibe esta foto como dato, sin volver a tocar la red.</para>
///
/// <para><b>Vencidas fuera, proximas a vencer adentro y marcadas (E7).</b> Una reserva vencida
/// suele ser antigua o no renovada por motivos que el informe no conoce: arrastrarla ensucia la
/// cifra sin aportar nada, asi que <see cref="ReservationDto.Expired"/> la excluye antes de
/// construir <see cref="FotoReservas.Reservas"/>. Una proxima a vencer
/// (<see cref="ReservationDto.Expiring"/>) si entra, marcada: es de gestion, el ahorro desaparece
/// solo si nadie decide renovarla.</para>
///
/// <para><b>El eje no medido, mismo patron que D9 (ver <see cref="EstadoRbac"/> y
/// <c>SeguridadCalculador</c>).</b> Sin credenciales activas, o con <c>errors</c> no vacio en la
/// lectura, <see cref="FotoReservas.Medido"/> es <c>false</c> y <see cref="FotoReservas.Reservas"/>
/// queda vacia: NUNCA se publica un balde en cero como si el cliente no tuviera reservas, que es
/// una afirmacion distinta y falsa. Igual que RBAC, ante una lectura parcial (alguna credencial
/// fallo) se descarta el conjunto completo en vez de publicar un numero que se ve como el total
/// sin serlo — mismo motivo que "el inventario de permisos no se pudo leer completo" en
/// <see cref="EstadoRbac.Resolver"/>.</para>
///
/// <para><b>Confirmado por consumidor, estimado por diferencia (E2).</b>
/// <see cref="IAzureReservationsClient.GetConsumersAsync"/> ya esta acotado a UNA reserva
/// especifica (el endpoint de Consumption es <c>{reservationId}/.../reservationDetails</c>): cada
/// <see cref="ConsumidorReserva"/> que devuelve, con horas usadas mayores a cero (mismo filtro que
/// <c>RiCoverageService.MatchConfirmed</c>), es un consumidor confirmado de ESA reserva puntual, no
/// un agregado por SKU/region/termino que haya que reconstruir. <see cref="ReservaActiva.UnidadesEstimadas"/>
/// es lo que queda de <see cref="ReservationDto.Quantity"/> sin un consumidor confirmado: no tiene
/// terna propia (no hay a que recurso atribuirselo), asi que Tarea 2 lo publica aparte, rotulado,
/// sin intentar cruzarlo contra facturacion.</para>
///
/// <para><b>Una falla puntual de Consumption no tumba toda la foto.</b> Si
/// <see cref="IAzureReservationsClient.GetConsumersAsync"/> lanza para una reserva especifica (un
/// permiso distinto al de listar reservas), esa reserva queda sin consumidores confirmados y toda
/// su cantidad pasa a estimada — mismo criterio que el <c>catch</c> silencioso de
/// <c>RiCoverageService.ComputeAsync</c> alrededor de la misma llamada. Es una degradacion mas
/// angosta que la del eje completo: una credencial que no puede leer <i>consumers</i> de una
/// reserva puntual no significa que el resto de la lectura de reservas sea invalida.</para>
/// </summary>
public sealed record FotoReservas(
    bool Medido,
    string Motivo,
    IReadOnlyList<object> Errores,
    /// <summary>El umbral de "proxima a vencer" (dias) con el que se resolvio
    /// <see cref="ReservaActiva.Expiring"/> en esta lectura puntual — el mismo que ya usa la
    /// pantalla de reservas (<c>CdcController</c>, alert_days=30 por defecto). Viaja DENTRO de la
    /// foto para que un informe reemitido mas adelante pueda decir que umbral se uso cuando se
    /// emitio, aunque el default cambie despues.</summary>
    int AlertDays,
    /// <summary>Instante (UTC) en que se hizo esta lectura en vivo contra Azure. Lo fija el
    /// recolector (IO): la calculadora de la Tarea 2 lo recibe como dato, no lo vuelve a leer.</summary>
    DateTime CapturadaEn,
    IReadOnlyList<ReservaActiva> Reservas);

/// <summary>Una reserva activa (no vencida) al momento de la foto, con sus consumidores
/// confirmados por terna. Ver el docstring de <see cref="FotoReservas"/> para el criterio de
/// exclusion de vencidas y de confirmado/estimado.</summary>
public sealed record ReservaActiva(
    string? ReservationId,
    string? Nombre,
    string? Producto,
    string? Region,
    int? Cantidad,
    string? Term,
    string? TermLabel,
    /// <summary>Cruda, formato "aaaa-MM-dd" (igual que <see cref="ReservationDto.ExpiresOn"/>): la
    /// Tarea 2 deriva de aca el inicio de la reserva (ExpiresOn menos Term), nunca del mes en que
    /// bajo la factura.</summary>
    string? ExpiresOn,
    int DaysRemaining,
    /// <summary>Proxima a vencer segun <see cref="FotoReservas.AlertDays"/>. Nunca <c>true</c> a la
    /// vez que la reserva esta vencida: las vencidas ya se excluyeron antes de llegar aca.</summary>
    bool Expiring,
    string? UtilizationLast,
    string? Utilization7d,
    IReadOnlyList<ConsumidorReserva> Consumidores,
    /// <summary><see cref="Cantidad"/> menos <see cref="Consumidores"/>.Count, piso en cero:
    /// unidades reservadas sin un consumidor confirmado. Sin terna propia — no hay a que recurso
    /// atribuirselo — asi que no entra al cruce contra facturacion de la Tarea 2.</summary>
    int UnidadesEstimadas,
    /// <summary><c>true</c> cuando la lectura de consumidores de ESTA reserva fallo, asi que
    /// <see cref="Consumidores"/> viene vacia por no haberse podido leer y no por no haberlos.
    ///
    /// <para>Sin esta marca los dos casos se ven identicos y llevan a decisiones opuestas: una
    /// reserva que todavia no tiene consumidores confirmados es normal, y una cuya lectura fallo
    /// significa que el ahorro esta subestimado y nadie lo sabe. Es el mismo criterio que los ejes
    /// de <c>EstadoRbac</c>: un cero que puede significar "no hay" o "no se midio" no se publica
    /// sin decir cual de los dos es.</para></summary>
    bool ConsumidoresNoLeidos);

/// <summary>Un recurso que consumio el beneficio de una reserva especifica, con la terna
/// (<see cref="SubscriptionId"/>/<see cref="ResourceGroup"/>/<see cref="ResourceName"/>) con la que
/// este modulo identifica un recurso en facturacion: mismos tres campos que
/// <see cref="ReservationConsumer"/>, sin heuristica de nombres de por medio.</summary>
public sealed record ConsumidorReserva(
    string InstanceId,
    string? ResourceName,
    string? ResourceGroup,
    string? SubscriptionId,
    string? SkuName,
    double UsedHours,
    string? LastSeen,
    int DaysSeen);

public static class ReservasRecolector
{
    /// <summary>Mismo default que <c>CdcController</c> (query <c>alert_days</c>) para la pantalla
    /// de reservas: E7 exige el MISMO umbral, no uno propio de este informe.</summary>
    public const int AlertDaysPorDefecto = 30;

    /// <summary>Ventana de dias para <see cref="IAzureReservationsClient.GetConsumersAsync"/>.
    /// Mismo valor que usa <c>RiCoverageService.ComputeAsync</c> para la misma llamada.</summary>
    private const int DiasConsumidoresPorDefecto = 30;

    public static async Task<FotoReservas> CapturarAsync(
        IReservationService reservations, IAzureReservationsClient client, int clientId,
        int alertDays = AlertDaysPorDefecto, int diasConsumidores = DiasConsumidoresPorDefecto,
        CancellationToken ct = default)
    {
        var capturadaEn = DateTime.UtcNow;

        var credenciales = await reservations.ActiveCredentialsAsync(clientId, ct);
        if (credenciales.Count == 0)
        {
            return new FotoReservas(Medido: false,
                Motivo: "El cliente no tiene credenciales de Azure activas: no hay reservas que se " +
                        "puedan leer de forma automatica, asi que este eje no se midio.",
                Errores: [], AlertDays: alertDays, CapturadaEn: capturadaEn, Reservas: []);
        }

        IReadOnlyList<ReservationDto> todas;
        IReadOnlyList<object> errores;
        try
        {
            (todas, errores) = await reservations.FetchAllAsync(credenciales, alertDays, includeUtilization: true, ct);
        }
        catch (Exception ex)
        {
            return new FotoReservas(Medido: false,
                Motivo: "La lectura de reservas fallo por completo: este eje no se midio.",
                Errores: [new { error = ex.GetType().Name }], AlertDays: alertDays,
                CapturadaEn: capturadaEn, Reservas: []);
        }

        // Mismo criterio que EstadoRbac.Resolver ante un inventario a medias: se descarta el
        // conjunto completo en vez de publicar un numero parcial que se ve como el total sin
        // serlo. No se distingue aca cual credencial fallo: cualquier error dentro del conjunto
        // vuelve todo el eje "no medido".
        if (errores.Count > 0)
        {
            return new FotoReservas(Medido: false,
                Motivo: "La lectura de reservas fallo para al menos una credencial: los datos de " +
                        "este cliente no estan completos, asi que el eje no se publica como medido.",
                Errores: errores, AlertDays: alertDays, CapturadaEn: capturadaEn, Reservas: []);
        }

        var activas = todas.Where(r => !r.Expired && !EstadoInactivo(r.State)).ToList();

        var reservas = new List<ReservaActiva>(activas.Count);
        foreach (var r in activas)
        {
            var (consumidores, noLeidos) = await ConsumidoresConfirmadosAsync(client, r, diasConsumidores, ct);
            var unidadesEstimadas = Math.Max(0, (r.Quantity ?? 0) - consumidores.Count);

            reservas.Add(new ReservaActiva(
                ReservationId: r.ReservationId, Nombre: r.Name, Producto: r.Product, Region: r.Region,
                Cantidad: r.Quantity, Term: r.Term, TermLabel: r.TermLabel, ExpiresOn: r.ExpiresOn,
                DaysRemaining: r.DaysRemaining, Expiring: r.Expiring, UtilizationLast: r.UtilizationLast,
                Utilization7d: r.Utilization7d, Consumidores: consumidores,
                UnidadesEstimadas: unidadesEstimadas, ConsumidoresNoLeidos: noLeidos));
        }

        var motivo = reservas.Count > 0
            ? "Las reservas activas se leyeron completas desde Azure."
            : "El cliente tiene credenciales activas y la lectura de reservas no encontro ninguna " +
              "reserva activa: es un cero legitimo, no una falla de lectura.";

        return new FotoReservas(Medido: true, Motivo: motivo, Errores: [], AlertDays: alertDays,
            CapturadaEn: capturadaEn, Reservas: reservas);
    }

    /// <summary>Consumidores confirmados de UNA reserva (ver el docstring de
    /// <see cref="FotoReservas"/>: "Confirmado por consumidor, estimado por diferencia"). Filtra
    /// horas usadas mayores a cero, mismo criterio que <c>RiCoverageService.MatchConfirmed</c>: un
    /// consumidor sin horas no es una confirmacion real. Si <c>GetConsumersAsync</c> lanza para
    /// esta reserva puntual, degrada a "sin confirmados" (toda la cantidad pasa a estimada) en vez
    /// de propagar la excepcion — mismo criterio que el catch silencioso de
    /// <c>RiCoverageService.ComputeAsync</c> alrededor de la misma llamada.</summary>
    private static async Task<(IReadOnlyList<ConsumidorReserva> Consumidores, bool NoLeidos)> ConsumidoresConfirmadosAsync(
        IAzureReservationsClient client, ReservationDto r, int diasConsumidores, CancellationToken ct)
    {
        // Sin identificador no hay a quien preguntarle: no es una falla de lectura, es una reserva
        // que Azure devolvio sin id. Se marca igual, porque el efecto sobre el ahorro es el mismo
        // (queda toda en estimado) y quien depure necesita saber por que.
        if (r.ReservationId is null) return ([], true);

        IReadOnlyList<ReservationConsumer> consumidores;
        try
        {
            consumidores = await client.GetConsumersAsync(r.CredentialId, r.ReservationId, diasConsumidores, ct);
        }
        catch
        {
            // La lectura de ESTA reserva fallo. Se degrada sin tumbar la foto, pero se marca: una
            // lista vacia por falla y una lista vacia por no haber consumidores llevan a
            // decisiones opuestas.
            return ([], true);
        }

        return (consumidores
            .Where(c => c.UsedHours > 0)
            .Select(c => new ConsumidorReserva(
                c.InstanceId, c.ResourceName, c.ResourceGroup, c.SubscriptionId, c.SkuName,
                c.UsedHours, c.LastSeen, c.DaysSeen))
            .ToList(), false);
    }

    /// <summary>Estados en los que una reserva ya no entrega beneficio, aunque su fecha de
    /// vencimiento todavia no haya pasado.
    ///
    /// <para>El plan solo nombra las vencidas porque es lo que se pidio, pero una reserva cancelada
    /// tampoco esta ahorrando nada. El motivo de fondo pesa mas que el filtro: sin esto, el informe
    /// de valor contaria el ahorro de una reserva cancelada mientras el motor de costos de la misma
    /// plataforma la ignora. Dos piezas del producto con dos definiciones del mismo concepto, cada
    /// una coherente consigo misma, es el defecto mas repetido de este modulo y el mas dificil de
    /// ver, porque ninguna de las dos esta mal por separado.</para>
    ///
    /// <para><b>Esta lista es un espejo</b> de <c>InactiveStates</c> en <c>RiCoverageService</c>,
    /// que es privado y vive en un archivo desplegado. Si alla cambia, aca tambien: son el mismo
    /// criterio y tienen que seguir siendolo.</para></summary>
    private static readonly HashSet<string> EstadosInactivos = new(StringComparer.OrdinalIgnoreCase)
    { "cancelled", "canceled", "expired", "failed" };

    private static bool EstadoInactivo(string? estado) =>
        estado is not null && EstadosInactivos.Contains(estado.Trim());
}
