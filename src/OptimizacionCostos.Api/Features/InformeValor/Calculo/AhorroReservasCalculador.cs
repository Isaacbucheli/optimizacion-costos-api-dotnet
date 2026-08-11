using System.Globalization;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Tarea 2 del plan de la entrega 2d (E2, E5): cruza <see cref="FotoReservas"/> (Tarea 1, IO)
/// contra las filas de facturacion por terna. Pura: no hace red ni lee la hora del corte —
/// <see cref="FotoReservas"/> ya trae resuelto todo lo que dependia de Azure o de un instante
/// (confirmado/estimado, vencida/proxima a vencer, utilizacion), asi que esta calculadora no
/// necesita ningun <see cref="ContextoInformeValor"/>.
///
/// <para><b>La ventana la marca la reserva, no la factura.</b> El inicio de cada reserva se deriva
/// de <see cref="ReservaActiva.ExpiresOn"/> menos <see cref="ReservaActiva.Term"/>
/// (<see cref="InicioDeReserva"/>): las filas de facturacion de ese recurso anteriores a ese mes
/// son "antes" (tarifa plena) y las de ese mes en adelante son "despues" (tarifa cubierta).
/// Deducir la frontera del mes en que la factura bajo seria inferir la causa desde el efecto — el
/// error que toda esta entrega corrige (ver <see cref="ReservasRecolector"/>).</para>
///
/// <para><b>Tarifa por hora, no un promedio mensual a secas.</b> El ahorro se pide "sobre la misma
/// cantidad de horas" que reporta la reserva (<see cref="ConsumidorReserva.UsedHours"/>, nunca
/// reconstruidas desde la factura): cada lado de la ventana se reduce a un $/hora —suma de
/// <c>Pvp</c> sobre el total de horas nominales de esos meses— para poder multiplicar por esas
/// horas. <see cref="HorasPorMes"/> (730) es la misma convencion que ya usa el resto del repo para
/// este pasaje ($/mes a $/hora): <c>IPricingConstants.HoursPerMonth()</c> en
/// <c>Features/CostEngine/Pricing</c> cae al mismo valor por defecto. Usar los dias reales de cada
/// mes en vez de esta convencion introduciria un segundo criterio para la misma conversion en el
/// mismo repo.</para>
///
/// <para><b>Confirmados arman la cifra; estimados se publican aparte.</b> Solo los pares
/// reserva-consumidor confirmados (terna conocida) entran a <see cref="AhorroReservasModelo.AhorroConfirmado"/>.
/// Las unidades estimadas (<see cref="ReservaActiva.UnidadesEstimadas"/>) no tienen terna: se
/// publican rotuladas en <see cref="AhorroReservasModelo.Estimados"/>, sin costo.</para>
///
/// <para><b>Discrepancia, no correccion.</b> Cuando la tarifa de despues no baja respecto de la de
/// antes para un recurso confirmado, eso se publica como discrepancia
/// (<see cref="AhorroReservasModelo.Discrepancias"/>): puede ser una reserva vencida que la lectura
/// no marco, un export de facturacion incompleto, o cualquier otro desajuste entre las dos
/// fuentes. Esta calculadora no elige la causa, y tampoco fuerza el <see cref="AhorroPorRecurso.Ahorro"/>
/// resultante a cero: publica el numero que sale, sea negativo, cero o positivo.</para>
/// </summary>
public static class AhorroReservasCalculador
{
    /// <summary>Horas nominales por mes para pasar un monto mensual a tarifa por hora. Mismo
    /// valor de fallback que <c>IPricingConstants.HoursPerMonth()</c> (Features/CostEngine/Pricing):
    /// convencion ya establecida en el repo, no un criterio nuevo de este bloque.</summary>
    private const decimal HorasPorMes = 730m;

    // Separador de unidad (U+001F), igual que NaturalKey.Hash: no aparece en un export de Excel,
    // asi que "s1|rg" + "1" y "s1" + "|rg1" (con un separador visible) no colisionarian entre si.
    private const char SeparadorTerna = '';

    public static AhorroReservasModelo Calcular(FotoReservas foto, IReadOnlyList<FacturacionRow> facturacion)
    {
        if (!foto.Medido)
            return new AhorroReservasModelo(
                Medido: false, Motivo: foto.Motivo, Errores: foto.Errores, AlertDays: foto.AlertDays,
                AhorroConfirmado: 0m, Confirmados: [], Estimados: [], Discrepancias: []);

        var porTerna = facturacion
            .GroupBy(f => ClaveTerna(f.SubscriptionId, f.ResourceGroup, f.ResourceName))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FacturacionRow>)g.ToList());

        var confirmados = new List<AhorroPorRecurso>();
        var estimados = new List<EstimadoPorReserva>();
        var discrepancias = new List<DiscrepanciaCobertura>();

        foreach (var reserva in foto.Reservas)
        {
            var inicio = InicioDeReserva(reserva.ExpiresOn, reserva.Term);

            foreach (var consumidor in reserva.Consumidores)
            {
                var (tarifaAntes, tarifaDespues, ahorro, motivoSinCalcular) = inicio is null
                    ? (null, null, (decimal?)null, "No se pudo derivar el inicio de la reserva: " +
                        $"el termino '{reserva.Term}' no se reconoce o falta la fecha de vencimiento.")
                    : CalcularAhorroDeRecurso(
                        porTerna.GetValueOrDefault(ClaveTerna(consumidor.SubscriptionId, consumidor.ResourceGroup, consumidor.ResourceName)),
                        inicio.Value, consumidor.UsedHours);

                confirmados.Add(new AhorroPorRecurso(
                    ResourceName: consumidor.ResourceName, ResourceGroup: consumidor.ResourceGroup,
                    SubscriptionId: consumidor.SubscriptionId, ReservationId: reserva.ReservationId,
                    ReservationName: reserva.Nombre, Term: reserva.Term,
                    InicioReserva: inicio?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    UsedHours: consumidor.UsedHours, UtilizationLast: reserva.UtilizationLast,
                    Utilization7d: reserva.Utilization7d, Expiring: reserva.Expiring,
                    TarifaAntesPorHora: tarifaAntes, TarifaDespuesPorHora: tarifaDespues,
                    Ahorro: ahorro, MotivoSinCalcular: motivoSinCalcular));

                if (ahorro is { } valor && valor <= 0m)
                    discrepancias.Add(new DiscrepanciaCobertura(
                        consumidor.ResourceName, consumidor.ResourceGroup, consumidor.SubscriptionId, reserva.ReservationId,
                        "La app confirma cobertura de reserva para este recurso, pero la facturacion " +
                        "no muestra una baja de tarifa despues del inicio de la reserva."));
            }

            if (reserva.UnidadesEstimadas > 0)
                estimados.Add(new EstimadoPorReserva(
                    reserva.ReservationId, reserva.Nombre, reserva.Producto, reserva.Region, reserva.Term,
                    reserva.UnidadesEstimadas));
        }

        var total = confirmados.Where(c => c.Ahorro is not null).Sum(c => c.Ahorro!.Value);

        return new AhorroReservasModelo(
            Medido: true, Motivo: foto.Motivo, Errores: foto.Errores, AlertDays: foto.AlertDays,
            AhorroConfirmado: Redondeo.ComoJs(total), Confirmados: confirmados, Estimados: estimados,
            Discrepancias: discrepancias);
    }

    /// <summary>Deriva el inicio de la reserva desde <see cref="ReservaActiva.ExpiresOn"/> ("aaaa-MM-dd")
    /// menos la duracion de <see cref="ReservaActiva.Term"/> ("P1Y"/"P3Y"/"P5Y", el formato que ya
    /// devuelve <c>AzureReservationsClient</c>). Null cuando cualquiera de los dos falta o no se
    /// reconoce: sin inicio no hay forma de partir la facturacion en antes/despues.</summary>
    private static DateOnly? InicioDeReserva(string? expiresOn, string? term)
    {
        if (string.IsNullOrWhiteSpace(expiresOn)) return null;
        if (!DateOnly.TryParseExact(expiresOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var vencimiento))
            return null;

        var anios = AniosDelTermino(term);
        return anios is null ? null : vencimiento.AddYears(-anios.Value);
    }

    /// <summary>"P{n}Y" -> n. No se limita a {1,3,5} (los terminos vistos hoy) para no acoplarse a
    /// una lista finita si Azure agrega un termino nuevo.</summary>
    private static int? AniosDelTermino(string? term)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;
        var t = term.Trim();
        if (t.Length < 3 || t[0] != 'P' || t[^1] != 'Y') return null;
        return int.TryParse(t[1..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    private static (decimal? TarifaAntes, decimal? TarifaDespues, decimal? Ahorro, string? Motivo) CalcularAhorroDeRecurso(
        IReadOnlyList<FacturacionRow>? filas, DateOnly inicio, double usedHours)
    {
        if (filas is null || filas.Count == 0)
            return (null, null, null, "Este recurso no aparece en la facturacion cargada.");

        var claveInicio = ClaveMes(inicio.Year, inicio.Month);
        var antes = filas.Where(f => ClaveMes(f.Year, f.Month) < claveInicio).ToList();
        var despues = filas.Where(f => ClaveMes(f.Year, f.Month) >= claveInicio).ToList();

        if (antes.Count == 0)
            return (null, null, null, "Sin facturacion anterior al inicio de la reserva para este recurso.");
        if (despues.Count == 0)
            return (null, null, null, "Sin facturacion posterior al inicio de la reserva para este recurso.");

        var tarifaAntes = antes.Sum(f => f.Pvp) / (antes.Count * HorasPorMes);
        var tarifaDespues = despues.Sum(f => f.Pvp) / (despues.Count * HorasPorMes);
        var ahorro = Redondeo.ComoJs((tarifaAntes - tarifaDespues) * (decimal)usedHours);

        return (tarifaAntes, tarifaDespues, ahorro, null);
    }

    private static int ClaveMes(int anio, int mes) => anio * 12 + mes;

    /// <summary>Misma normalizacion que <c>RiCoverageService.Norm</c> para su propio cruce (recorte
    /// de espacios + minusculas): la terna que reporta Consumption y la que trae un export de
    /// facturacion pueden diferir en mayusculas aunque sea el mismo recurso.</summary>
    private static string ClaveTerna(string? subscriptionId, string? resourceGroup, string? resourceName) =>
        string.Join(SeparadorTerna, Norm(subscriptionId), Norm(resourceGroup), Norm(resourceName));

    private static string Norm(string? v) => (v ?? string.Empty).Trim().ToLowerInvariant();
}
