using System.Globalization;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Tarea 2 del plan de la entrega 2d (E2, E5), mas el ajuste de E9 (tarea 5, la costura con los
/// baldes 2 y 3): cruza <see cref="FotoReservas"/> (Tarea 1, IO) contra las filas de facturacion por
/// terna. Pura: no hace red ni lee la hora del corte — <see cref="FotoReservas"/> ya trae resuelto
/// todo lo que dependia de Azure o de un instante (confirmado/estimado, vencida/proxima a vencer,
/// utilizacion).
///
/// <para><b>E9: dos ventanas, dos preguntas distintas, nunca mezcladas.</b> El ahorro "desde el
/// inicio de la reserva" (<see cref="AhorroPorRecurso.Ahorro"/>, sin cambios de esta tarea) sigue
/// partiendo la facturacion completa del recurso en un antes/despues anclado a
/// <see cref="InicioDeReserva"/>: es la cifra correcta para el panel de reservas (E5), con su propia
/// ventana declarada. Pero los baldes 2 y 3 (<see cref="AtribucionCalculador"/>) miden sobre una
/// ventana FIJA para todo el portafolio (<paramref name="mesesParciales"/> mas
/// <see cref="ContextoInformeValor.PeriodStart"/>/<see cref="ContextoInformeValor.PeriodEnd"/>), y
/// sumar una cifra medida desde el inicio propio de cada reserva con cifras medidas sobre esa
/// ventana fija seria sumar mediciones de ventanas distintas — aritmeticamente posible, semanticamente
/// falso. Por eso esta tarea agrega una SEGUNDA medicion,
/// <see cref="AhorroPorRecurso.AporteAlPeriodo"/>: el mismo tipo de ahorro por reserva, pero sobre la
/// MISMA ventana fija que usan los baldes 2 y 3, y solo para las reservas cuyo inicio realmente cae
/// DENTRO de esa ventana (<see cref="AhorroPorRecurso.ExplicaElPeriodo"/>) — una reserva que arranco
/// antes de que la ventana empiece ya tenia al recurso cubierto durante TODO el periodo, asi que no
/// explica ninguna variacion que se vea adentro (ver <see cref="EsElegiblePorFecha"/>).</para>
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

    /// <summary>Mismo minimo que <c>AtribucionCalculador.MinMesesParaVariacion</c> (tres para la
    /// ventana base mas tres para la de cierre), y por el mismo motivo: sin esta historia, la
    /// ventana fija del informe no existe, y ninguna reserva puede "explicar" nada dentro de una
    /// ventana que no se pudo calcular. Constante propia, no reusada: esa es <c>private</c> en una
    /// clase de otra tarea (ver el comentario de clase de <c>AtribucionCalculador</c> sobre por que
    /// prefiere reimplementar tres lineas a ensanchar la visibilidad ajena).</summary>
    private const int MinMesesParaVentana = 6;

    private const int TamanoVentanaCierre = 3;

    /// <param name="mesesParciales">Tiene que ser <see cref="ConsumoModelo.MesesParciales"/> del
    /// bloque de consumo ya calculado para el mismo informe: la deteccion de meses parciales no se
    /// repite acá, mismo motivo que <c>AtribucionCalculador</c> (para que los dos bloques de la
    /// ventana fija —este y el de <see cref="AtribucionCalculador"/>— nunca puedan discrepar sobre
    /// que mes es parcial).</param>
    /// <param name="contexto">Solo para derivar la ventana fija de E9 (<see cref="ContextoInformeValor.PeriodStart"/>/
    /// <see cref="ContextoInformeValor.PeriodEnd"/>): el resto del calculo (ahorro desde el inicio de
    /// cada reserva) sigue sin necesitarlo, igual que antes de esta tarea.</param>
    public static AhorroReservasModelo Calcular(
        FotoReservas foto, IReadOnlyList<FacturacionRow> facturacion,
        IReadOnlyList<string> mesesParciales, ContextoInformeValor contexto)
    {
        if (!foto.Medido)
            return new AhorroReservasModelo(
                Medido: false, Motivo: foto.Motivo, Errores: foto.Errores, AlertDays: foto.AlertDays,
                AhorroConfirmado: 0m, Confirmados: [], Estimados: [], Discrepancias: [],
                AporteAlPeriodo: 0m, RecursosQueExplicanElPeriodo: []);

        var porTerna = facturacion
            .GroupBy(f => ClaveTerna(f.SubscriptionId, f.ResourceGroup, f.ResourceName))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<FacturacionRow>)g.ToList());

        // E9: la MISMA ventana fija que arma AtribucionCalculador, sobre la MISMA facturacion y el
        // MISMO contexto — null cuando no hay para eso seis meses no parciales, y en ese caso ninguna
        // reserva puede explicar nada dentro de un periodo que no se pudo medir.
        var ventana = VentanaFijaDelInforme(facturacion, mesesParciales, contexto);

        var confirmados = new List<AhorroPorRecurso>();
        var estimados = new List<EstimadoPorReserva>();
        var discrepancias = new List<DiscrepanciaCobertura>();
        // Deduplicado por terna D11 (E9): si el mismo recurso aparece como confirmado bajo dos
        // reservas y las dos son elegibles, el aporte se cuenta UNA sola vez (no depende de la
        // reserva, solo del propio recurso y de la ventana).
        var aportesPorRecurso = new Dictionary<string, decimal>();

        foreach (var reserva in foto.Reservas)
        {
            var inicio = InicioDeReserva(reserva.ExpiresOn, reserva.Term);

            foreach (var consumidor in reserva.Consumidores)
            {
                var filasDelRecurso = porTerna.GetValueOrDefault(
                    ClaveTerna(consumidor.SubscriptionId, consumidor.ResourceGroup, consumidor.ResourceName));

                var (tarifaAntes, tarifaDespues, ahorro, motivoSinCalcular) = inicio is null
                    ? (null, null, (decimal?)null, "No se pudo derivar el inicio de la reserva: " +
                        $"el termino '{reserva.Term}' no se reconoce o falta la fecha de vencimiento.")
                    : CalcularAhorroDeRecurso(filasDelRecurso, inicio.Value, consumidor.UsedHours);

                // E9: la fecha decide SI la reserva explica algo del periodo; si es elegible, el
                // aporte se mide sobre la MISMA ventana fija (nunca la tarifa por hora de arriba,
                // que mide desde el propio inicio de la reserva).
                var explica = inicio is not null && ventana is { } v && EsElegiblePorFecha(inicio.Value, v);
                decimal? aporteAlPeriodo = null;
                if (explica)
                {
                    var aporteCrudo = AporteSobreVentana(filasDelRecurso, ventana!.Value);
                    aporteAlPeriodo = Redondeo.ComoJs(aporteCrudo);
                    var idD11 = IdD11ParaExclusion(consumidor, filasDelRecurso);
                    aportesPorRecurso[idD11] = aporteCrudo; // sobreescribe, no suma: dedup por terna
                }

                confirmados.Add(new AhorroPorRecurso(
                    ResourceName: consumidor.ResourceName, ResourceGroup: consumidor.ResourceGroup,
                    SubscriptionId: consumidor.SubscriptionId, ReservationId: reserva.ReservationId,
                    ReservationName: reserva.Nombre, Term: reserva.Term,
                    InicioReserva: inicio?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    UsedHours: consumidor.UsedHours, UtilizationLast: reserva.UtilizationLast,
                    Utilization7d: reserva.Utilization7d, Expiring: reserva.Expiring,
                    TarifaAntesPorHora: tarifaAntes, TarifaDespuesPorHora: tarifaDespues,
                    Ahorro: ahorro, MotivoSinCalcular: motivoSinCalcular,
                    ExplicaElPeriodo: explica, AporteAlPeriodo: aporteAlPeriodo));

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

        // E1: se redondea UNA sola vez, desde la suma SIN redondear de los aportes crudos por
        // recurso — nunca la suma de los AporteAlPeriodo individuales, que ya vienen redondeados
        // para mostrarse (mismo criterio que AtribucionCalculador.ArmarBalde).
        var aporteAlPeriodoTotal = Redondeo.ComoJs(aportesPorRecurso.Values.Sum());

        return new AhorroReservasModelo(
            Medido: true, Motivo: foto.Motivo, Errores: foto.Errores, AlertDays: foto.AlertDays,
            AhorroConfirmado: Redondeo.ComoJs(total), Confirmados: confirmados, Estimados: estimados,
            Discrepancias: discrepancias, AporteAlPeriodo: aporteAlPeriodoTotal,
            RecursosQueExplicanElPeriodo: [.. aportesPorRecurso.Keys]);
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

    /// <summary>Los meses (clave "aaaa-MM") de la ventana fija de E9, partidos en base/cierre EXACTO
    /// igual que <c>AtribucionCalculador.Calcular</c>: mismo filtro D0
    /// (<see cref="ConsumoCalculador.EnRango"/>) sobre <paramref name="facturacion"/>, mismos
    /// <paramref name="mesesParciales"/> ya resueltos, mismo minimo de seis meses y mismo tamano de
    /// ventana de cierre (tres). Null cuando no hay suficiente historia — el mismo caso en el que
    /// <c>AtribucionCalculador.Calcular</c> devuelve null.</summary>
    private static (IReadOnlyList<string> Base, IReadOnlyList<string> Fin)? VentanaFijaDelInforme(
        IReadOnlyList<FacturacionRow> facturacion, IReadOnlyList<string> mesesParciales, ContextoInformeValor contexto)
    {
        var mk = facturacion
            .Where(f => ConsumoCalculador.EnRango(f.Year, f.Month, contexto.PeriodStart, contexto.PeriodEnd))
            .Select(f => ConsumoCalculador.Ym(f.Year, f.Month))
            .Distinct()
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

        var parcialSet = mesesParciales.ToHashSet();
        var mesesNoParciales = mk.Where(m => !parcialSet.Contains(m)).ToList();
        if (mesesNoParciales.Count < MinMesesParaVentana) return null;

        var ventanaBase = mesesNoParciales.Take(mesesNoParciales.Count - TamanoVentanaCierre).ToList();
        var ventanaFin = mesesNoParciales.Skip(mesesNoParciales.Count - TamanoVentanaCierre).ToList();
        return (ventanaBase, ventanaFin);
    }

    /// <summary>
    /// E9, la regla central: la fecha de inicio de la reserva decide SI explica algo dentro del
    /// periodo, nunca cuanto. Elegible solo cuando <paramref name="inicio"/> cae estrictamente
    /// DESPUES del primer mes de <c>ventana.Base</c> (si no, toda la ventana —base y cierre— ya
    /// tenia al recurso cubierto: no hay "antes" que ver adentro) y no despues del ultimo mes de
    /// <c>ventana.Fin</c> (si no, la cobertura ni empezo dentro del periodo del informe). La
    /// comparacion es sobre las mismas claves "aaaa-MM" ordinales que usa todo el modulo para meses
    /// (<c>ConsumoCalculador.Ym</c>), nunca sobre <see cref="DateOnly"/>: el mes es la unidad de la
    /// ventana, un dia dentro del mes de inicio no cambia si ese mes es "antes" o "despues".
    /// </summary>
    private static bool EsElegiblePorFecha(DateOnly inicio, (IReadOnlyList<string> Base, IReadOnlyList<string> Fin) ventana)
    {
        var inicioMes = inicio.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return string.CompareOrdinal(inicioMes, ventana.Base[0]) > 0
            && string.CompareOrdinal(inicioMes, ventana.Fin[^1]) <= 0;
    }

    /// <summary>
    /// El aporte de un recurso a la variacion del PERIODO (E9): promedio de <c>ventana.Base</c> menos
    /// promedio de <c>ventana.Fin</c>, sobre la facturacion CRUDA del recurso (<see cref="FacturacionRow.Pvp"/>
    /// por mes, denso — un mes sin fila cuenta como cero, igual que <c>AtribucionCalculador</c>), NUNCA
    /// la tarifa por hora que usa <see cref="CalcularAhorroDeRecurso"/> para el ahorro desde el propio
    /// inicio de la reserva: son dos preguntas distintas sobre dos ventanas distintas, y esta es la
    /// que se puede sumar con los baldes 2 y 3. Cruda (sin redondear): quien llama redondea una sola
    /// vez, sobre la suma de varios recursos (E1).
    /// </summary>
    private static decimal AporteSobreVentana(
        IReadOnlyList<FacturacionRow>? filasDelRecurso, (IReadOnlyList<string> Base, IReadOnlyList<string> Fin) ventana)
    {
        var porMes = (filasDelRecurso ?? [])
            .GroupBy(f => ConsumoCalculador.Ym(f.Year, f.Month))
            .ToDictionary(g => g.Key, g => g.Sum(f => f.Pvp));

        var baseAvg = ventana.Base.Select(m => porMes.GetValueOrDefault(m)).Average();
        var finAvg = ventana.Fin.Select(m => porMes.GetValueOrDefault(m)).Average();
        return baseAvg - finAvg;
    }

    /// <summary>
    /// El id de terna D11/E6 EXACTO que usa <c>AtribucionCalculador.Calcular</c> para agrupar
    /// facturacion (<c>(subscriptionId ?? subscriptionName-o-"(sin suscripcion)") + "|" + resourceGroup
    /// + "|" + resourceName</c>, SIN normalizar mayusculas/minusculas): tiene que coincidir caracter
    /// por caracter con lo que esa clase construye desde <paramref name="facturacion"/>, porque el
    /// <c>HashSet&lt;string&gt;.Contains</c> que hace la exclusion (E3) no tolera diferencias de
    /// mayusculas. Por eso, cuando hay una fila de facturacion para este recurso
    /// (<paramref name="filas"/>, ya resuelta por <see cref="ClaveTerna"/> — que SI normaliza, para
    /// encontrar la fila aunque la Consumption API y el export difieran en mayusculas), el id se
    /// construye desde ESA fila cruda, nunca desde <paramref name="consumidor"/>: son la MISMA fuente
    /// que usa <c>AtribucionCalculador</c> internamente, asi que coinciden por construccion. Sin
    /// ninguna fila (el recurso no aparece en facturacion), se cae al propio consumidor: ese id nunca
    /// va a coincidir con ninguna clave de <c>AtribucionCalculador.Calcular</c> (que solo agrupa por
    /// filas de facturacion existentes), asi que agregarlo al conjunto de exclusion no tiene efecto —
    /// no hay nada que excluir de un recurso que no factura.
    /// </summary>
    private static string IdD11ParaExclusion(ConsumidorReserva consumidor, IReadOnlyList<FacturacionRow>? filas)
    {
        if (filas is { Count: > 0 })
        {
            var f = filas[0];
            var subNombre = string.IsNullOrWhiteSpace(f.SubscriptionName) ? "(sin suscripción)" : f.SubscriptionName!;
            var subId = f.SubscriptionId ?? subNombre;
            return subId + "|" + (f.ResourceGroup ?? "") + "|" + (f.ResourceName ?? "");
        }

        return (consumidor.SubscriptionId ?? "") + "|" + (consumidor.ResourceGroup ?? "") + "|" + (consumidor.ResourceName ?? "");
    }

    /// <summary>Misma normalizacion que <c>RiCoverageService.Norm</c> para su propio cruce (recorte
    /// de espacios + minusculas): la terna que reporta Consumption y la que trae un export de
    /// facturacion pueden diferir en mayusculas aunque sea el mismo recurso.</summary>
    private static string ClaveTerna(string? subscriptionId, string? resourceGroup, string? resourceName) =>
        string.Join(SeparadorTerna, Norm(subscriptionId), Norm(resourceGroup), Norm(resourceName));

    private static string Norm(string? v) => (v ?? string.Empty).Trim().ToLowerInvariant();
}
