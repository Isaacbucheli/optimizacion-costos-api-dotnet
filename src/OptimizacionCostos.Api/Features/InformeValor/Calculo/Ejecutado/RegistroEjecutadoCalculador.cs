using System.Globalization;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;
using OptimizacionCostos.Api.Features.Optimization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;

/// <summary>
/// Tarea 4 del plan de la entrega 6: las filas del registro de lo ejecutado (la unidad de la PPT
/// de MERCANTIL), a partir de tres fuentes independientes — el barrido de optimización (Tarea
/// previa de la entrega 5, <see cref="BarridoResueltoRecolector"/>), la matriz WAF
/// (<see cref="HallazgoResueltoRecolector"/>) y las reservas activas (Tareas 1 y 3 de esta
/// entrega, <see cref="FotoReservas"/>/<see cref="ReservasFacturadasModelo"/>). Pura, sin IO ni
/// reloj (ver <c>SinRelojDelSistemaTests</c>, que escanea <c>Calculo/</c> completo).
///
/// <para><b>Terna, no nombre (D11).</b> Igual que <c>ConsumoCalculador</c>/<c>AtribucionCalculador</c>,
/// un recurso se identifica por <c>(subscriptionId ?? nombre-de-suscripción) + "|" + grupo + "|" +
/// nombre</c>, SIN normalizar mayúsculas: barrido y matriz llegan con identificadores que salen de
/// Azure (Resource Graph / Advisor) igual que la facturación, así que la comparación cruda ya
/// coincide por construcción — a diferencia de <c>ReservasFacturadasCalculador</c>, que cruza
/// contra un archivo de Excel y sí necesita normalizar.</para>
///
/// <para><b>Regla 4, "meses completos": sparse, no denso.</b> <c>ConsumoCalculador</c>/
/// <c>AtribucionCalculador</c> arman una ventana COMPARTIDA de todo el portafolio y rellenan con
/// cero los meses en que un recurso puntual no facturó (necesario ahí: miden si un recurso
/// "murió"). Esta calculadora mide una pregunta distinta — el delta de UN recurso alrededor de SU
/// propio mes de ejecución — así que solo usa los meses en que ESE recurso realmente facturó: sin
/// relleno de ceros, porque un mes sin fila no es "cero gasto", es "no hay dato para promediar".</para>
///
/// <para><b>Regla 4, meses parciales: solo los forzados, nunca la heurística automática.</b> La
/// detección automática (D5 de <c>ConsumoCalculador</c>) vive en el bloque de consumo y exige su
/// propio umbral y ventana de todo el portafolio; no se puede re-ejecutar aquí sin duplicar esa
/// lógica sobre un universo de meses distinto (el de un solo recurso). Por eso esta calculadora
/// solo excluye <see cref="ContextoInformeValor.MesesParcialesForzados"/> (cuando el consultor los
/// declaró) y siempre excluye el propio mes de ejecución (que es mixto por definición: antes y
/// después del hecho dentro del mismo mes).</para>
///
/// <para><b>Un delta negativo jamás entra como monto facturado.</b> Regla 5: si la facturación
/// posterior al mes de ejecución es MÁS cara que la anterior, ese incremento lo explica la
/// variación de consumo (entrega 2d), no el titular de lo ejecutado — publicar un "ahorro"
/// negativo bajo <c>fuenteMonto="facturado"</c> sería falso. El barrido cae a su estimado propio en
/// ese caso; la matriz, que no tiene estimado propio, queda sin monto con motivo.</para>
///
/// <para><b>Regla 7, la dedup deja rastro.</b> Un recurso resuelto en el mismo mes por barrido Y
/// matriz, o cubierto por una reserva confirmada, no hace desaparecer la fila perdedora: se
/// publica igual (fuera de la aritmética, por el mismo contrato de <c>MotivoSinMonto</c> que
/// documenta <see cref="AccionEjecutada"/>), con el motivo nombrando qué otra fuente ya se llevó
/// el crédito. Nunca un descarte silencioso.</para>
/// </summary>
public static class RegistroEjecutadoCalculador
{
    public static (IReadOnlyList<AccionEjecutada> Filas, RegistroEjes Ejes) Calcular(
        RegistroBarrido barrido,
        IReadOnlyList<HallazgoResueltoFila> hallazgosMatriz,
        ReservasFacturadasModelo reservasFacturadas,
        FotoReservas fotoReservas,
        IReadOnlyList<FacturacionRow> facturacion,
        ContextoInformeValor contexto)
    {
        var (porMesPorTerna, categoriaPorTerna) = AgruparFacturacionPorTerna(facturacion, contexto);

        // Regla 7, segunda mitad (E3 de la 2d): un recurso confirmado como consumidor de una
        // reserva activa siempre le atribuye el ahorro a la reserva, nunca al barrido/matriz.
        var ternasCubiertasPorReserva = fotoReservas.Medido
            ? fotoReservas.Reservas
                .SelectMany(r => r.Consumidores)
                .Where(c => c.SubscriptionId is not null)
                .Select(c => Terna(c.SubscriptionId!, c.ResourceGroup, c.ResourceName))
                .ToHashSet()
            : [];

        var filasBarrido = CalcularBarrido(barrido, porMesPorTerna, categoriaPorTerna, contexto, ternasCubiertasPorReserva);

        // Regla 7, primera mitad: mismo recurso resuelto por barrido Y matriz en el mismo mes ->
        // gana el barrido (trae estimado de respaldo, la matriz no).
        var barridoPorTernaYMes = filasBarrido.Select(x => (x.Terna, x.Fila.MesEjecucion)).ToHashSet();

        var filasMatriz = CalcularMatriz(
            hallazgosMatriz, porMesPorTerna, categoriaPorTerna, contexto, ternasCubiertasPorReserva, barridoPorTernaYMes);

        var (filasReserva, reservasSinInicio) = CalcularReservas(fotoReservas, reservasFacturadas);

        var todas = filasBarrido.Select(x => x.Fila)
            .Concat(filasMatriz)
            .Concat(filasReserva)
            .ToList();

        var ejes = new RegistroEjes(
            BarridoMedido: barrido.Medido,
            BarridoMotivo: barrido.Motivo,
            ReservasMedidas: reservasFacturadas.Medido,
            ReservasMotivo: MotivoDelEjeDeReservas(reservasFacturadas, reservasSinInicio),
            Indeterminadas: todas.Count(f => f.Autoria == "indeterminada"));

        return (todas, ejes);
    }

    // ── Regla 1 ──

    private static List<(AccionEjecutada Fila, string Terna)> CalcularBarrido(
        RegistroBarrido barrido,
        Dictionary<string, Dictionary<string, decimal>> porMesPorTerna,
        Dictionary<string, string?> categoriaPorTerna,
        ContextoInformeValor contexto,
        HashSet<string> ternasCubiertasPorReserva)
    {
        var filas = new List<(AccionEjecutada, string)>();
        if (!barrido.Medido) return filas;

        foreach (var b in barrido.Filas)
        {
            var rg = ExtraerResourceGroup(b.AzureResourceId);
            var terna = Terna(b.SubscriptionId, rg, b.ResourceName);
            var mesEjecucion = ConsumoCalculador.Ym((short)b.ResueltoEn.Year, (byte)b.ResueltoEn.Month);
            var autoria = b.ResolvedByKind switch
            {
                "manual" => "declarada",
                "auto" => "automatica",
                _ => "indeterminada",
            };
            var categoria = CategoriaEjecutado.Resolver("barrido", b.CheckId, categoriaPorTerna.GetValueOrDefault(terna));

            decimal? montoCrudo = null;
            string? fuenteMonto = null;
            string? motivoSinMonto;

            if (ternasCubiertasPorReserva.Contains(terna))
            {
                motivoSinMonto = "Este recurso está cubierto por una reserva activa: el ahorro se le " +
                    "atribuye a la reserva, no al barrido (mismo criterio de la atribución de consumo, E3).";
            }
            else
            {
                var delta = CalcularDelta(porMesPorTerna, terna, mesEjecucion, contexto.MesesParcialesForzados);
                if (delta is > 0m)
                {
                    montoCrudo = delta;
                    fuenteMonto = "facturado";
                    motivoSinMonto = null;
                }
                else if (b.EstimatedMonthlySavings is > 0m)
                {
                    montoCrudo = b.EstimatedMonthlySavings;
                    fuenteMonto = "estimado";
                    motivoSinMonto = null;
                }
                else
                {
                    motivoSinMonto = "La facturación no muestra reducción y el barrido no estimó ahorro.";
                }
            }

            var fila = new AccionEjecutada(
                Fuente: "barrido",
                Oportunidad: NombreDelCheck(b.CheckId),
                Categoria: categoria,
                SubscriptionId: b.SubscriptionId,
                ResourceGroup: rg,
                ResourceName: b.ResourceName,
                MesEjecucion: mesEjecucion,
                MesFin: null,
                MontoMensual: montoCrudo is null ? null : Redondeo.ComoJs(montoCrudo.Value),
                FuenteMonto: fuenteMonto,
                MotivoSinMonto: motivoSinMonto,
                Autoria: autoria);

            filas.Add((fila, terna));
        }

        return filas;
    }

    private static string NombreDelCheck(string checkId) =>
        OptimizationChecks.Registered.FirstOrDefault(c => c.CheckId == checkId)?.Name ?? checkId;

    /// <summary>Segmento tras <c>resourceGroups/</c> del id ARM (case-insensitive). Un id sin ese
    /// segmento (hallazgo a nivel de suscripción) devuelve <c>null</c>: el cruce por terna contra
    /// facturación simplemente no encuentra al recurso, y la fila cae a estimado (regla 5) —
    /// comportamiento correcto, no un error.</summary>
    private static string? ExtraerResourceGroup(string azureResourceId)
    {
        var segmentos = azureResourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segmentos.Length - 1; i++)
            if (string.Equals(segmentos[i], "resourceGroups", StringComparison.OrdinalIgnoreCase))
                return segmentos[i + 1];
        return null;
    }

    // ── Regla 2 (y 6, precedencia del monto sin estimado propio) ──

    private static List<AccionEjecutada> CalcularMatriz(
        IReadOnlyList<HallazgoResueltoFila> hallazgosMatriz,
        Dictionary<string, Dictionary<string, decimal>> porMesPorTerna,
        Dictionary<string, string?> categoriaPorTerna,
        ContextoInformeValor contexto,
        HashSet<string> ternasCubiertasPorReserva,
        HashSet<(string Terna, string Mes)> barridoPorTernaYMes)
    {
        var filas = new List<AccionEjecutada>();

        foreach (var h in hallazgosMatriz)
        {
            if (h.ResolvedAt is not { } fecha) continue; // regla 2: sin fecha no se puede ubicar
            if (fecha < contexto.PeriodStart || fecha > contexto.PeriodEnd) continue; // D0

            var terna = Terna(h.SubscriptionId, h.ResourceGroup, h.ResourceName);
            var mesEjecucion = ConsumoCalculador.Ym((short)fecha.Year, (byte)fecha.Month);
            var categoria = CategoriaEjecutado.Resolver("matriz", null, categoriaPorTerna.GetValueOrDefault(terna));

            decimal? montoCrudo = null;
            string? fuenteMonto = null;
            string? motivoSinMonto;

            if (ternasCubiertasPorReserva.Contains(terna))
            {
                motivoSinMonto = "Este recurso está cubierto por una reserva activa: el ahorro se le " +
                    "atribuye a la reserva, no a la matriz (mismo criterio de la atribución de consumo, E3).";
            }
            else if (barridoPorTernaYMes.Contains((terna, mesEjecucion)))
            {
                // Regla 7: gana el barrido (trae estimado de respaldo); esta fila queda visible,
                // anotada, fuera de la aritmética — nunca borrada en silencio.
                motivoSinMonto = "Este recurso ya se registró desde el barrido de optimización en el " +
                    "mismo mes: se evita contar el ahorro dos veces.";
            }
            else
            {
                var delta = CalcularDelta(porMesPorTerna, terna, mesEjecucion, contexto.MesesParcialesForzados);
                if (delta is > 0m)
                {
                    montoCrudo = delta;
                    fuenteMonto = "facturado";
                    motivoSinMonto = null;
                }
                else
                {
                    // Regla 6: la matriz no trae una estimación propia (el ahorro de Advisor vive
                    // en Postura), así que sin delta positivo no hay ningún respaldo al que caer.
                    motivoSinMonto = "La matriz no trae una estimación propia de ahorro (el ahorro de " +
                        "Advisor vive en Postura), y la facturación no muestra una reducción medible " +
                        "para este recurso.";
                }
            }

            filas.Add(new AccionEjecutada(
                Fuente: "matriz",
                Oportunidad: h.Hallazgo,
                Categoria: categoria,
                SubscriptionId: h.SubscriptionId,
                ResourceGroup: h.ResourceGroup,
                ResourceName: h.ResourceName,
                MesEjecucion: mesEjecucion,
                MesFin: null,
                MontoMensual: montoCrudo is null ? null : Redondeo.ComoJs(montoCrudo.Value),
                FuenteMonto: fuenteMonto,
                MotivoSinMonto: motivoSinMonto,
                Autoria: "declarada"));
        }

        return filas;
    }

    // ── Regla 3 ──

    private static (List<AccionEjecutada> Filas, int SinInicio) CalcularReservas(
        FotoReservas fotoReservas, ReservasFacturadasModelo reservasFacturadas)
    {
        var filas = new List<AccionEjecutada>();
        var sinInicio = 0;
        if (!fotoReservas.Medido) return (filas, sinInicio);

        var ahorroPorReserva = reservasFacturadas.Filas
            .Where(f => f.ReservationId is not null)
            .GroupBy(f => f.ReservationId!)
            .ToDictionary(g => g.Key, g => g.Where(f => f.AhorroMes is not null).Select(f => f.AhorroMes!.Value).ToList());

        foreach (var reserva in fotoReservas.Reservas)
        {
            var inicio = AhorroReservasCalculador.InicioDeReserva(reserva.ExpiresOn, reserva.Term);
            if (inicio is null) { sinInicio++; continue; } // regla 3: sin inicio derivable, se descarta

            var mesEjecucion = ConsumoCalculador.Ym((short)inicio.Value.Year, (byte)inicio.Value.Month);
            var mesFin = MesDe(reserva.ExpiresOn);

            var ahorros = reserva.ReservationId is not null ? ahorroPorReserva.GetValueOrDefault(reserva.ReservationId) : null;
            decimal? montoCrudo = null;
            string? fuenteMonto = null;
            string? motivoSinMonto = null;
            if (ahorros is { Count: > 0 })
            {
                montoCrudo = ahorros.Sum();
                fuenteMonto = "facturado";
            }
            else
            {
                motivoSinMonto = "Sin línea de reserva en el archivo de evolución: no se pudo cruzar el " +
                    "cargo mensual de esta reserva contra la facturación.";
            }

            filas.Add(new AccionEjecutada(
                Fuente: "reserva",
                Oportunidad: reserva.Nombre ?? reserva.Producto ?? reserva.ReservationId ?? "(reserva sin nombre)",
                Categoria: CategoriaEjecutado.Resolver("reserva", null, null),
                SubscriptionId: null,
                ResourceGroup: null,
                ResourceName: null,
                MesEjecucion: mesEjecucion,
                MesFin: mesFin,
                MontoMensual: montoCrudo is null ? null : Redondeo.ComoJs(montoCrudo.Value),
                FuenteMonto: fuenteMonto,
                MotivoSinMonto: motivoSinMonto,
                Autoria: "declarada"));
        }

        return (filas, sinInicio);
    }

    private static string? MesDe(string? fechaIso)
    {
        if (string.IsNullOrWhiteSpace(fechaIso)) return null;
        if (!DateOnly.TryParseExact(fechaIso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
            return null;
        return ConsumoCalculador.Ym((short)fecha.Year, (byte)fecha.Month);
    }

    /// <summary>Regla 8: refleja el modelo de la Tarea 3, que ya cascada la degradación de la
    /// Tarea 1 (<see cref="ReservasFacturadasModelo"/> degrada con el motivo de
    /// <see cref="FotoReservas"/> cuando la foto no midió, y con su propio motivo cuando la foto sí
    /// midió pero la evolución no trae líneas de reserva). Cuando el eje SÍ midió pero una o más
    /// reservas se descartaron de las filas por no tener inicio derivable (regla 3), el motivo lo
    /// dice igual: "medido" no es lo mismo que "todo entró".</summary>
    private static string? MotivoDelEjeDeReservas(ReservasFacturadasModelo reservasFacturadas, int reservasSinInicio)
    {
        if (!reservasFacturadas.Medido) return reservasFacturadas.Motivo;
        if (reservasSinInicio > 0)
            return $"{reservasSinInicio} reserva(s) activa(s) no se pudieron ubicar en el tiempo (sin " +
                   "fecha de inicio derivable: término no reconocido o sin fecha de vencimiento) y no " +
                   "entran al registro.";
        return null;
    }

    // ── Regla 4 ──

    private static (Dictionary<string, Dictionary<string, decimal>> PorMes, Dictionary<string, string?> Categoria)
        AgruparFacturacionPorTerna(IReadOnlyList<FacturacionRow> facturacion, ContextoInformeValor contexto)
    {
        var porMes = new Dictionary<string, Dictionary<string, decimal>>();
        var categoria = new Dictionary<string, string?>();

        foreach (var f in facturacion)
        {
            if (!ConsumoCalculador.EnRango(f.Year, f.Month, contexto.PeriodStart, contexto.PeriodEnd)) continue;

            // D11, mismo respaldo a nombre que ConsumoCalculador/AtribucionCalculador.
            var subName = string.IsNullOrWhiteSpace(f.SubscriptionName) ? "(sin suscripción)" : f.SubscriptionName!;
            var subId = f.SubscriptionId ?? subName;
            var id = Terna(subId, f.ResourceGroup, f.ResourceName);

            if (!porMes.TryGetValue(id, out var mesesDeLaTerna)) { mesesDeLaTerna = []; porMes[id] = mesesDeLaTerna; }
            var mes = ConsumoCalculador.Ym(f.Year, f.Month);
            mesesDeLaTerna[mes] = mesesDeLaTerna.GetValueOrDefault(mes) + f.Pvp;

            // Primera categoría no vacía vista para esta terna, en el orden del insumo: solo
            // importa para el respaldo de CategoriaEjecutado.Resolver cuando el checkId no mapea
            // (barrido) o no existe (matriz) — nunca participa de la aritmética del monto.
            if (!categoria.TryGetValue(id, out var actual) || actual is null)
                if (!string.IsNullOrWhiteSpace(f.Category)) categoria[id] = f.Category;
        }

        return (porMes, categoria);
    }

    /// <summary>Regla 4: promedio de meses completos ANTERIORES a <paramref name="mesEjecucion"/>
    /// menos promedio de los POSTERIORES, sobre la facturación cruda (sin relleno de ceros: ver el
    /// comentario de clase). "Completos" excluye el propio mes de ejecución (mixto por definición)
    /// y los <paramref name="mesesParcialesForzados"/> declarados por el consultor — la detección
    /// automática de parciales no se repite aquí (ver el comentario de clase). <c>null</c> sin al
    /// menos un mes completo a cada lado (sin fila de facturación para la terna, o con historia
    /// insuficiente de un lado) — el llamador no distingue el motivo puntual: las reglas 5 y 6 ya
    /// fijan el texto único que se publica cuando no hay delta medible.</summary>
    private static decimal? CalcularDelta(
        Dictionary<string, Dictionary<string, decimal>> porMesPorTerna, string terna, string mesEjecucion,
        IReadOnlyList<string>? mesesParcialesForzados)
    {
        if (!porMesPorTerna.TryGetValue(terna, out var porMes) || porMes.Count == 0) return null;

        var forzados = new HashSet<string>(mesesParcialesForzados ?? []);
        var mesesCompletos = porMes.Keys.Where(m => m != mesEjecucion && !forzados.Contains(m)).ToList();

        var antes = mesesCompletos.Where(m => string.CompareOrdinal(m, mesEjecucion) < 0).ToList();
        var despues = mesesCompletos.Where(m => string.CompareOrdinal(m, mesEjecucion) > 0).ToList();
        if (antes.Count == 0 || despues.Count == 0) return null;

        var promedioAntes = antes.Average(m => porMes[m]);
        var promedioDespues = despues.Average(m => porMes[m]);
        return promedioAntes - promedioDespues;
    }

    private static string Terna(string subscriptionId, string? resourceGroup, string? resourceName) =>
        subscriptionId + "|" + (resourceGroup ?? "") + "|" + (resourceName ?? "");
}
