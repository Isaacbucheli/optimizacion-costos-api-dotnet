using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Tareas 3 y 4 del plan de la entrega 2d (E1, E3, E4, E6): de la variación de gasto de cada
/// recurso entre una ventana base y una ventana de cierre, cuánto se puede atribuir a una
/// recomendación resuelta de la matriz (balde 2, E3) y cómo se abre el resto por mecanismo (balde
/// 3, E4). El balde 1 (por reserva, E2) es de otra tarea de esta misma entrega: ver
/// <paramref name="recursosConReservaConfirmada"/> más abajo para el punto de encuentro.
///
/// <para><b>Ventanas: las mismas que D3 (<see cref="ConsumoCalculador.CalcularAhorro"/>), pero por
/// RECURSO en vez de por categoría, y con PROMEDIO en las dos puntas.</b> Base = todos los meses no
/// parciales menos los últimos tres; cierre = promedio de esos últimos tres. D3 sigue usando
/// mediana para su línea base (es lo que la hace robusta a un mes atípico, y esa robustez importa
/// para el titular); esta descomposición no puede: la suma de las medianas por recurso no
/// reconstruye la mediana del total (no es una función lineal), así que atribuir con mediana rompe
/// la invariante que esta clase existe para cerrar (E1). El promedio sí es lineal: la suma de los
/// promedios por recurso, sobre la misma ventana, es exactamente el promedio del total.</para>
///
/// <para><b>Meses parciales: se reciben resueltos, no se vuelven a detectar.</b>
/// <paramref name="mesesParciales"/> tiene que ser <see cref="ConsumoModelo.MesesParciales"/> del
/// bloque de consumo YA calculado para el mismo informe (mismo insumo, mismo contexto): la
/// heurística de detección automática (D0/D5) vive en <c>ConsumoCalculador</c> y no se duplica acá,
/// para que los dos bloques nunca puedan discrepar sobre qué mes es parcial. Sin excluir el mes
/// parcial, su monto artificialmente bajo (todavía no terminó de facturar) infla "recurso que dejó
/// de facturar" y subestima el promedio de cierre de todos los demás — el mismo defecto que D5 ya
/// corrigió para las bajas del bloque de consumo, acá aplicado a cada mecanismo.</para>
///
/// <para><b>Identidad de un recurso: la terna, nunca el nombre solo (E6).</b> Mismo id que ya usa
/// <c>ConsumoCalculador</c> para altas/bajas (D11): <c>(subscription_id ?? nombre-de-suscripción) +
/// "|" + grupo + "|" + nombre</c>. Con identidad por nombre, un recurso que muere en un grupo y un
/// homónimo que nace en otro se ven como uno solo que "cambió de precio": el mismo dólar que debería
/// caer en <see cref="SinAtribuirModelo.DejoDeFacturar"/> más <see cref="SinAtribuirModelo.Nuevo"/>
/// (dos mecanismos, dos historias) se atribuye en cambio a <see cref="SinAtribuirModelo.VivoCuestaMenos"/>
/// o <see cref="SinAtribuirModelo.VivoCuestaMas"/> (una historia falsa de continuidad). Medido contra
/// datos reales, ese error cambia la atribución de un mecanismo por un orden de magnitud (ver el
/// plan); <c>AtribucionCalculadorTests</c> lo reproduce con datos sintéticos.</para>
/// </summary>
public static class AtribucionCalculador
{
    /// <summary>Mismo mínimo que <c>ConsumoCalculador.MinMesesParaAhorro</c> (tres para la ventana
    /// base más tres para la de cierre) y por el mismo motivo: menos historia no alcanza para que
    /// "ventana base" signifique algo, ni siquiera con promedio. Constante propia (no se reusa la de
    /// <c>ConsumoCalculador</c>, que es <c>private</c>) para no tener que ensanchar la visibilidad de
    /// una clase de otra entrega por una constante — ver el informe de la tarea.</summary>
    private const int MinMesesParaVariacion = 6;

    private const int TamanoVentanaCierre = 3;

    /// <summary>
    /// <c>null</c> cuando no hay filas en el rango del informe, o cuando hay menos de
    /// <see cref="MinMesesParaVariacion"/> meses no parciales (igual que
    /// <c>ConsumoCalculador.CalcularAhorro</c>: sin suficiente historia no se publica nada, en vez de
    /// una cifra que aparenta precisión sobre una ventana demasiado corta).
    /// </summary>
    /// <param name="facturacion">Mismas filas que recibe <c>ConsumoCalculador.Calcular</c>: esta
    /// función aplica su propio filtro D0 (<see cref="ConsumoCalculador.EnRango"/>), no asume que ya
    /// vengan filtradas.</param>
    /// <param name="hallazgosResueltos">De <see cref="HallazgoResueltoRecolector"/>, sin filtrar por
    /// fecha: el filtro de <see cref="ContextoInformeValor.PeriodStart"/>/<see cref="ContextoInformeValor.PeriodEnd"/>
    /// (D0) se aplica acá, sobre <see cref="HallazgoResueltoFila.ResolvedAt"/>.</param>
    /// <param name="mesesParciales">Ver el comentario de clase: tiene que ser
    /// <see cref="ConsumoModelo.MesesParciales"/> del bloque de consumo ya calculado para el mismo
    /// informe.</param>
    /// <param name="recursosConReservaConfirmada">
    /// <b>El punto de encuentro con la Tarea 1/2 de esta entrega (balde de reservas).</b> El
    /// identificador de cada recurso, en el MISMO formato que usa esta clase internamente:
    /// <c>(subscriptionId ?? subscriptionName-o-"(sin suscripción)") + "|" + resourceGroup + "|" +
    /// resourceName</c> (idéntico al id de D11 en <c>ConsumoCalculador</c>). Quien ensambla el
    /// informe pasa acá los recursos que el balde de reservas ya confirmó cubiertos (los
    /// "confirmados" de E2, nunca los "estimados": ver el plan, sección E2) — normalmente
    /// construidos a partir de <c>ReservationConsumer.SubscriptionId</c>/<c>ResourceGroup</c>/
    /// <c>ResourceName</c>, que ya vienen con <c>SubscriptionId</c> real (la Consumption API de
    /// Azure no lo omite), así que en el caso común coincide con esta terna sin necesitar ningún
    /// respaldo a nombre. Lista vacía (nunca null) cuando el balde de reservas todavía no corrió o
    /// no encontró coincidencias: con lista vacía, esta función no excluye a nadie.
    /// </param>
    public static AtribucionModelo? Calcular(
        IReadOnlyList<FacturacionRow> facturacion,
        IReadOnlyList<HallazgoResueltoFila> hallazgosResueltos,
        IReadOnlyList<string> mesesParciales,
        IReadOnlySet<string> recursosConReservaConfirmada,
        ContextoInformeValor contexto)
    {
        var enRango = facturacion
            .Where(f => ConsumoCalculador.EnRango(f.Year, f.Month, contexto.PeriodStart, contexto.PeriodEnd))
            .ToList();
        if (enRango.Count == 0) return null;

        var (porRecurso, identidad, mk) = AgruparPorRecursoYMes(enRango);

        var parcialSet = mesesParciales.ToHashSet();
        var mesesNoParciales = mk.Where(m => !parcialSet.Contains(m)).ToList();
        if (mesesNoParciales.Count < MinMesesParaVariacion) return null;

        var ventanaBase = mesesNoParciales.Take(mesesNoParciales.Count - TamanoVentanaCierre).ToList();
        var ventanaFin = mesesNoParciales.Skip(mesesNoParciales.Count - TamanoVentanaCierre).ToList();

        var hallazgosPorRecurso = AgruparHallazgosResueltosEnRango(hallazgosResueltos, contexto);

        var porRecomendacion = new List<(AtribucionRecurso Item, decimal Delta)>();
        var dejoDeFacturar = new List<(AtribucionRecurso Item, decimal Delta)>();
        var vivoCuestaMenos = new List<(AtribucionRecurso Item, decimal Delta)>();
        var vivoCuestaMas = new List<(AtribucionRecurso Item, decimal Delta)>();
        var nuevo = new List<(AtribucionRecurso Item, decimal Delta)>();
        var excluidosPorReserva = new List<(AtribucionRecurso Item, decimal Delta)>();

        foreach (var id in porRecurso.Keys)
        {
            var porMes = porRecurso[id];
            var (subId, subName, rg, res) = identidad[id];

            var baseAvg = ventanaBase.Select(m => porMes.GetValueOrDefault(m)).Average();
            var finAvg = ventanaFin.Select(m => porMes.GetValueOrDefault(m)).Average();
            // Solo facturo en un mes parcial que la ventana ya excluyó: sin señal analizable, no
            // entra a ningun balde (no es "dejo de facturar" ni "nuevo": no hay ventana en la que
            // se lo pueda ver facturando).
            if (baseAvg == 0m && finAvg == 0m) continue;

            var delta = baseAvg - finAvg;
            var recomendaciones = hallazgosPorRecurso.GetValueOrDefault(id, []);

            AtribucionRecurso ConTexto(IReadOnlyList<string> recs) => new(
                SubscriptionId: subId, SubscriptionName: subName, ResourceGroup: rg, ResourceName: res,
                BaseAvg: Redondeo.ComoJs(baseAvg), FinAvg: Redondeo.ComoJs(finAvg), Delta: Redondeo.ComoJs(delta),
                Recomendaciones: recs);

            // E3: gana la reserva. Se decide ANTES de mirar la matriz: un recurso con recomendacion
            // resuelta Y cobertura de reserva confirmada cae acá, nunca en PorRecomendacion.
            if (recursosConReservaConfirmada.Contains(id))
            {
                excluidosPorReserva.Add((ConTexto([]), delta));
                continue;
            }

            if (recomendaciones.Count > 0)
            {
                porRecomendacion.Add((ConTexto(recomendaciones), delta));
                continue;
            }

            if (finAvg == 0m) dejoDeFacturar.Add((ConTexto([]), delta));
            else if (baseAvg == 0m) nuevo.Add((ConTexto([]), delta));
            else if (delta >= 0m) vivoCuestaMenos.Add((ConTexto([]), delta));
            else vivoCuestaMas.Add((ConTexto([]), delta));
        }

        var baldePorRecomendacion = ArmarBalde(porRecomendacion);
        var baldeDejoDeFacturar = ArmarBalde(dejoDeFacturar);
        var baldeVivoCuestaMenos = ArmarBalde(vivoCuestaMenos);
        var baldeVivoCuestaMas = ArmarBalde(vivoCuestaMas);
        var baldeNuevo = ArmarBalde(nuevo);

        // E1: el total de cada nivel es la SUMA de baldes YA redondeados, nunca una cifra redondeada
        // de forma independiente a partir de los deltas crudos. Sumar decimal de 2 cifras da otro
        // decimal exacto de 2 cifras: la igualdad de la invariante es aritmética, no un acierto de
        // los numeros del test.
        var sinAtribuirTotal = baldeDejoDeFacturar.Total + baldeVivoCuestaMenos.Total
            + baldeVivoCuestaMas.Total + baldeNuevo.Total;
        var sinAtribuir = new SinAtribuirModelo(
            baldeDejoDeFacturar, baldeVivoCuestaMenos, baldeVivoCuestaMas, baldeNuevo, sinAtribuirTotal);

        var crecimiento = -(baldeVivoCuestaMas.Total + baldeNuevo.Total);
        var variacionTotal = baldePorRecomendacion.Total + sinAtribuir.Total;

        return new AtribucionModelo(
            PorRecomendacion: baldePorRecomendacion,
            SinAtribuir: sinAtribuir,
            Crecimiento: crecimiento,
            VariacionTotal: variacionTotal,
            ExcluidosPorReserva: OrdenarPorImpacto(excluidosPorReserva));
    }

    /// <summary>Id D11/E6: idéntico al que arma <c>ConsumoCalculador.Calcular</c> para altas/bajas
    /// (<c>(f.SubscriptionId ?? sub) + "|" + rg + "|" + res</c>), a propósito no reusado desde ahí
    /// (es una variable local de esa función, no un método): reimplementarlo acá evita ensanchar la
    /// superficie pública de una clase de otra entrega por tres líneas de agrupación.</summary>
    private static (
        Dictionary<string, Dictionary<string, decimal>> PorRecurso,
        Dictionary<string, (string SubId, string SubName, string Rg, string Res)> Identidad,
        List<string> Meses)
        AgruparPorRecursoYMes(IReadOnlyList<FacturacionRow> enRango)
    {
        var porRecurso = new Dictionary<string, Dictionary<string, decimal>>();
        var identidad = new Dictionary<string, (string SubId, string SubName, string Rg, string Res)>();
        var mesesSet = new HashSet<string>();

        foreach (var f in enRango)
        {
            var mes = ConsumoCalculador.Ym(f.Year, f.Month);
            mesesSet.Add(mes);

            var subName = string.IsNullOrWhiteSpace(f.SubscriptionName) ? "(sin suscripción)" : f.SubscriptionName!;
            var rg = f.ResourceGroup ?? "";
            var res = f.ResourceName ?? "";
            var subId = f.SubscriptionId ?? subName;
            var id = subId + "|" + rg + "|" + res;

            if (!porRecurso.TryGetValue(id, out var porMes))
            {
                porMes = [];
                porRecurso[id] = porMes;
                identidad[id] = (subId, subName, rg, res);
            }
            porMes[mes] = porMes.GetValueOrDefault(mes) + f.Pvp;
        }

        var meses = mesesSet.OrderBy(x => x, StringComparer.Ordinal).ToList();
        return (porRecurso, identidad, meses);
    }

    /// <summary>D0 (Recolector amplio, calculadora filtra): descarta los hallazgos sin
    /// <see cref="HallazgoResueltoFila.ResolvedAt"/> (no se pueden ubicar en ningún período) y los
    /// que resolvieron fuera del rango del informe. Agrupa por la terna en el MISMO formato que
    /// <see cref="AgruparPorRecursoYMes"/>: <see cref="HallazgoResueltoFila.SubscriptionId"/> nunca
    /// es null (NOT NULL en <c>waf_resource_finding</c>), así que no necesita el respaldo a nombre
    /// que sí hace falta del lado de facturación.</summary>
    private static Dictionary<string, List<string>> AgruparHallazgosResueltosEnRango(
        IReadOnlyList<HallazgoResueltoFila> hallazgosResueltos, ContextoInformeValor contexto)
    {
        var porRecurso = new Dictionary<string, List<string>>();
        foreach (var h in hallazgosResueltos)
        {
            if (h.ResolvedAt is not { } fecha) continue;
            if (fecha < contexto.PeriodStart || fecha > contexto.PeriodEnd) continue;

            var id = h.SubscriptionId + "|" + h.ResourceGroup + "|" + h.ResourceName;
            if (!porRecurso.TryGetValue(id, out var lista))
            {
                lista = [];
                porRecurso[id] = lista;
            }
            var etiqueta = string.IsNullOrWhiteSpace(h.MatrixCode) ? h.Hallazgo : $"{h.MatrixCode}: {h.Hallazgo}";
            if (!lista.Contains(etiqueta)) lista.Add(etiqueta);
        }
        return porRecurso;
    }

    /// <summary>E1: <see cref="AtribucionBalde.Total"/> se redondea UNA vez, a partir de la suma SIN
    /// redondear de los deltas crudos (no de la suma de los <see cref="AtribucionRecurso.Delta"/> ya
    /// redondeados individualmente, que es un número distinto —ver el comentario de clase de
    /// <see cref="AtribucionRecurso"/>).</summary>
    private static AtribucionBalde ArmarBalde(List<(AtribucionRecurso Item, decimal Delta)> items) => new(
        Total: Redondeo.ComoJs(items.Sum(x => x.Delta)),
        Cantidad: items.Count,
        Recursos: OrdenarPorImpacto(items));

    /// <summary>Los mayores movimientos primero (en valor absoluto), para que el consultor vea el
    /// principal contribuyente de cada balde sin tener que ordenar la lista él mismo.</summary>
    private static List<AtribucionRecurso> OrdenarPorImpacto(List<(AtribucionRecurso Item, decimal Delta)> items) =>
        items.OrderByDescending(x => Math.Abs(x.Delta)).Select(x => x.Item).ToList();
}
