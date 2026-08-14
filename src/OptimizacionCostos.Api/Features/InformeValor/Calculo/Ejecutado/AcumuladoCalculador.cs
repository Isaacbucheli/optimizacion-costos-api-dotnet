using System.Globalization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;

/// <summary>
/// Tarea 5 del plan de la entrega 6: el acumulado de lo ejecutado, titular del informe (decisión
/// 2026-08-13). Reproduce al centavo la serie de la PPT de MERCANTIL (ver
/// <c>docs/2026-08-13-analisis-ppt-mercantil-informe-valor.md</c> y el fixture homónimo en
/// <c>AcumuladoCalculadorTests</c>). Pura y sin reloj (ver <c>SinRelojDelSistemaTests</c>, que
/// escanea <c>Calculo/</c> completo): el corte llega en <see cref="ContextoInformeValor.Corte"/>.
///
/// <para><b>El modelo de la PPT no es "gasto acumulado" en el sentido contable habitual.</b>
/// <c>tasaVigente(m)</c> es la suma de todas las filas con monto que YA se ejecutaron para el mes
/// <c>m</c> (<c>MesEjecucion &lt;= m</c>) y que TODAVÍA están vigentes en <c>m</c> (sin
/// <see cref="AccionEjecutada.MesFin"/>, o con <c>m &lt;= MesFin</c>, INCLUSIVE: una reserva deja
/// de sumar recién el mes siguiente a su vencimiento). El acumulado de la PPT vuelve a sumar esa
/// tasa vigente mes a mes (<c>acumulado(m) = acumulado(m-1) + tasaVigente(m)</c>): no es "tasa ×
/// meses restantes", es la doble acumulación que efectivamente dibuja la diapositiva 2 de MERCANTIL
/// — reproducirla tal cual es el contrato de esta tarea, no corregirla.</para>
///
/// <para><b>La ventana es la del informe, no la historia de cada acción.</b> Una acción ejecutada
/// antes de <see cref="ContextoInformeValor.PeriodStart"/> aporta su tasa completa a
/// <c>tasaVigente</c> desde el primer mes del rango (su ahorro se sigue percibiendo), pero
/// <see cref="EjecutadoModelo.PorOportunidad"/> y la composición facturado/estimado solo cuentan los
/// meses de esa acción que caen DENTRO del rango — nunca los meses anteriores, que no forman parte
/// de este informe.</para>
///
/// <para><b>La proyección reusa la misma <c>tasaVigente</c>, evaluada a futuro.</b> Como la función
/// solo depende de comparar <c>MesEjecucion</c>/<c>MesFin</c> contra un mes cualquiera, sirve tal
/// cual para meses posteriores al corte (todas las filas ya están ejecutadas para esa fecha): no
/// hace falta una segunda fórmula. La proyección arranca en <see cref="EjecutadoModelo.AcumuladoTotal"/>
/// (el acumulado del último mes del rango) y suma, mes a mes, hasta diciembre del año del corte —
/// nunca "tasa del cierre × 12": si una reserva vence en el medio, la proyección lo refleja.</para>
/// </summary>
public static class AcumuladoCalculador
{
    public static EjecutadoModelo Calcular(
        IReadOnlyList<AccionEjecutada> filas,
        RegistroEjes ejes,
        ReservasFacturadasModelo reservas,
        decimal? gastoTotalRango,
        ContextoInformeValor contexto)
    {
        var mesesDelRango = MesesDelRango(contexto);

        var serie = new List<IReadOnlyList<object?>>();
        var acumuladoCorriendo = 0m;
        foreach (var mes in mesesDelRango)
        {
            var tasa = TasaVigenteEn(filas, ToOrdinal(mes));
            acumuladoCorriendo += tasa;
            serie.Add((IReadOnlyList<object?>)[mes, tasa, acumuladoCorriendo]);
        }

        var acumuladoTotal = serie.Count == 0 ? 0m : (decimal)serie[^1][2]!;
        var tasaVigenteCierre = serie.Count == 0 ? 0m : (decimal)serie[^1][1]!;

        var porCategoria = ConstruirPorCategoria(filas, mesesDelRango);
        var porOportunidad = ConstruirPorOportunidad(filas, mesesDelRango);
        var (montoFacturado, montoEstimado) = ConstruirComposicion(filas, mesesDelRango);
        var filasSinMonto = filas.Count(f => f.MontoMensual is null);

        var (proyeccion, proyeccionFin) = ConstruirProyeccion(filas, contexto, acumuladoTotal);

        var pctGastoPeriodo = gastoTotalRango is > 0m
            ? Math.Round(acumuladoTotal / gastoTotalRango.Value * 100m, 1, MidpointRounding.AwayFromZero)
            : (decimal?)null;

        var medido = filas.Count > 0 || ejes.BarridoMedido || ejes.ReservasMedidas;
        var motivo = medido ? null : CombinarMotivos(ejes);

        return new EjecutadoModelo(
            Medido: medido,
            Motivo: motivo,
            Filas: filas,
            Serie: serie,
            PorOportunidad: porOportunidad,
            PorCategoria: porCategoria,
            AcumuladoTotal: acumuladoTotal,
            TasaVigenteCierre: tasaVigenteCierre,
            PctGastoPeriodo: pctGastoPeriodo,
            MontoFacturado: montoFacturado,
            MontoEstimado: montoEstimado,
            FilasSinMonto: filasSinMonto,
            Proyeccion: proyeccion,
            ProyeccionFinDeAnio: proyeccionFin,
            Reservas: reservas,
            Ejes: ejes);
    }

    /// <summary>La tasa vigente en el mes <paramref name="mOrdinal"/>: suma de
    /// <see cref="AccionEjecutada.MontoMensual"/> entre las filas con monto, ya ejecutadas para ese
    /// mes (<c>MesEjecucion &lt;= m</c>) y todavía vigentes (<c>MesFin</c> nulo, o <c>m &lt;=
    /// MesFin</c>). Sirve igual para meses del rango histórico que para meses de proyección: no
    /// distingue entre los dos, solo compara ordinales de mes.</summary>
    private static decimal TasaVigenteEn(IReadOnlyList<AccionEjecutada> filas, int mOrdinal)
    {
        var total = 0m;
        foreach (var f in filas)
        {
            if (f.MontoMensual is not decimal monto) continue;
            if (ToOrdinal(f.MesEjecucion) > mOrdinal) continue;
            if (f.MesFin is not null && mOrdinal > ToOrdinal(f.MesFin)) continue;
            total += monto;
        }
        return total;
    }

    /// <summary>Cuántos meses de <paramref name="mesesDelRango"/> tiene vigente esta fila: los
    /// mismos límites que <see cref="TasaVigenteEn"/> (inicio en <c>MesEjecucion</c>, fin inclusive
    /// en <c>MesFin</c>), pero contando meses del rango en vez de sumar montos. Es la base de
    /// <see cref="EjecutadoModelo.PorOportunidad"/> y de la composición facturado/estimado: una
    /// acción anterior al rango cuenta solo los meses que SÍ caen dentro de él (D del comentario de
    /// clase: "aporta tasa, no historia").</summary>
    private static int MesesActivosDentroDelRango(AccionEjecutada fila, List<string> mesesDelRango)
    {
        var inicio = ToOrdinal(fila.MesEjecucion);
        var fin = fila.MesFin is null ? (int?)null : ToOrdinal(fila.MesFin);
        return mesesDelRango.Count(m =>
        {
            var o = ToOrdinal(m);
            return o >= inicio && (fin is null || o <= fin);
        });
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> ConstruirPorCategoria(
        IReadOnlyList<AccionEjecutada> filas, List<string> mesesDelRango)
    {
        var resultado = new Dictionary<string, IReadOnlyDictionary<string, decimal>>();
        foreach (var cat in filas.Select(f => f.Categoria).Distinct())
        {
            var filasDeLaCategoria = filas.Where(f => f.Categoria == cat).ToList();
            var porMes = new Dictionary<string, decimal>();
            foreach (var mes in mesesDelRango)
                porMes[mes] = TasaVigenteEn(filasDeLaCategoria, ToOrdinal(mes));
            resultado[cat] = porMes;
        }
        return resultado;
    }

    /// <summary>Invariante 2 (test): la suma de esta lista da exactamente
    /// <see cref="EjecutadoModelo.AcumuladoTotal"/>, porque la contribución de cada fila es la misma
    /// que ya explica ese total — <c>monto × mesesActivosDentroDelRango</c> — solo que agrupada por
    /// <see cref="AccionEjecutada.Oportunidad"/> en vez de por mes.</summary>
    private static List<IReadOnlyList<object?>> ConstruirPorOportunidad(
        IReadOnlyList<AccionEjecutada> filas, List<string> mesesDelRango)
    {
        var acumuladoPorOportunidad = new Dictionary<string, decimal>();
        var ordenOportunidades = new List<string>();
        foreach (var f in filas)
        {
            var contribucion = f.MontoMensual is decimal monto
                ? monto * MesesActivosDentroDelRango(f, mesesDelRango)
                : 0m;
            if (!acumuladoPorOportunidad.ContainsKey(f.Oportunidad)) ordenOportunidades.Add(f.Oportunidad);
            acumuladoPorOportunidad[f.Oportunidad] = acumuladoPorOportunidad.GetValueOrDefault(f.Oportunidad) + contribucion;
        }
        return ordenOportunidades
            .OrderByDescending(o => acumuladoPorOportunidad[o])
            .Select(o => (IReadOnlyList<object?>)[o, acumuladoPorOportunidad[o]])
            .ToList();
    }

    /// <summary>Composición declarada del total (nunca implícita): la misma contribución por fila
    /// que <see cref="ConstruirPorOportunidad"/>, partida por <see cref="AccionEjecutada.FuenteMonto"/>
    /// en vez de por oportunidad. <c>MontoFacturado + MontoEstimado == AcumuladoTotal</c> siempre que
    /// toda fila con monto traiga su fuente rotulada (contrato de <see cref="AccionEjecutada"/>).</summary>
    private static (decimal Facturado, decimal Estimado) ConstruirComposicion(
        IReadOnlyList<AccionEjecutada> filas, List<string> mesesDelRango)
    {
        var facturado = 0m;
        var estimado = 0m;
        foreach (var f in filas)
        {
            if (f.MontoMensual is not decimal monto) continue;
            var contribucion = monto * MesesActivosDentroDelRango(f, mesesDelRango);
            if (f.FuenteMonto == "facturado") facturado += contribucion;
            else if (f.FuenteMonto == "estimado") estimado += contribucion;
        }
        return (facturado, estimado);
    }

    /// <summary>Meses desde el siguiente al mes de <see cref="ContextoInformeValor.Corte"/> hasta
    /// diciembre del año del corte, y el acumulado proyectado de cada uno (misma recursión que la
    /// serie histórica, arrancando en <paramref name="acumuladoTotal"/> en vez de en cero). Corte en
    /// diciembre da una lista vacía y <c>ProyeccionFinDeAnio = acumuladoTotal</c>, sin excepción.</summary>
    private static (List<IReadOnlyList<object?>> Proyeccion, decimal Fin) ConstruirProyeccion(
        IReadOnlyList<AccionEjecutada> filas, ContextoInformeValor contexto, decimal acumuladoTotal)
    {
        var meses = MesesDeProyeccion(contexto);
        if (meses.Count == 0) return ([], acumuladoTotal);

        var proyeccion = new List<IReadOnlyList<object?>>();
        var acumulado = acumuladoTotal;
        foreach (var mes in meses)
        {
            var tasa = TasaVigenteEn(filas, ToOrdinal(mes));
            acumulado += tasa;
            proyeccion.Add((IReadOnlyList<object?>)[mes, tasa, acumulado]);
        }
        return (proyeccion, acumulado);
    }

    private static string? CombinarMotivos(RegistroEjes ejes)
    {
        var motivos = new List<string>();
        if (!string.IsNullOrWhiteSpace(ejes.BarridoMotivo)) motivos.Add(ejes.BarridoMotivo!);
        if (!string.IsNullOrWhiteSpace(ejes.ReservasMotivo)) motivos.Add(ejes.ReservasMotivo!);
        return motivos.Count == 0 ? null : string.Join(" ", motivos);
    }

    // ── Meses "aaaa-MM": ordinal year*12+month, único punto de conversión del módulo ──

    private static List<string> MesesDelRango(ContextoInformeValor contexto)
    {
        var inicio = contexto.PeriodStart.Year * 12 + contexto.PeriodStart.Month;
        var fin = contexto.PeriodEnd.Year * 12 + contexto.PeriodEnd.Month;
        var meses = new List<string>();
        for (var o = inicio; o <= fin; o++) meses.Add(FromOrdinal(o));
        return meses;
    }

    private static List<string> MesesDeProyeccion(ContextoInformeValor contexto)
    {
        var corte = contexto.Corte.Year * 12 + contexto.Corte.Month;
        var finDeAnio = contexto.Corte.Year * 12 + 12;
        var meses = new List<string>();
        for (var o = corte + 1; o <= finDeAnio; o++) meses.Add(FromOrdinal(o));
        return meses;
    }

    private static int ToOrdinal(string ym)
    {
        var anio = int.Parse(ym[..4], CultureInfo.InvariantCulture);
        var mes = int.Parse(ym[5..7], CultureInfo.InvariantCulture);
        return anio * 12 + mes;
    }

    private static string FromOrdinal(int ordinal)
    {
        var anio = (ordinal - 1) / 12;
        var mes = ordinal - anio * 12;
        return anio.ToString("D4", CultureInfo.InvariantCulture) + "-" + mes.ToString("D2", CultureInfo.InvariantCulture);
    }
}
