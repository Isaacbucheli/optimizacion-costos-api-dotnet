using System.Globalization;
using System.Text.RegularExpressions;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Bloque de postura (Tarea 6 del plan de la entrega 2b): produce <see cref="PosturaModelo"/> a
/// partir de <see cref="AdvisorFila"/> (Azure Advisor) y <see cref="RetiroFila"/> (retiros de
/// Azure), corrigiendo D7, D8, D11 y D13. Puerto de <c>calcAdvisor</c> en
/// <c>docs/Plantilla-Dashboard-BIT.html</c>, salvo donde una de esas cuatro decisiones cambia el
/// resultado a propósito.
///
/// <para>Solo lee <see cref="ContextoInformeValor.Corte"/> del contexto: D0 (filtro por período) no
/// está entre las decisiones de esta tarea porque Advisor y los retiros de Azure son estado
/// ACTIVO —una foto de ahora—, no un evento histórico dentro de un rango de facturación o de
/// casos. <see cref="ContextoInformeValor.PeriodStart"/>/<see cref="ContextoInformeValor.PeriodEnd"/>
/// se reciben por uniformidad con los otros cuatro bloques, no porque este los necesite.</para>
/// </summary>
public static class PosturaCalculadora
{
    private const string TipoReserva = "RI";
    private const string TipoSavingsPlan = "SP";
    private const string TipoOtro = "OTRO";
    private const string OtrosTipos = "Otros tipos";
    private const int MaximoTiposRecurso = 14;
    private const int MaximoTop = 15;
    private const int LargoMaximoRecomendacion = 105;
    private const int LargoCorteRecomendacion = 102;
    private const int DiasVentanaTresMeses = 92;
    private const int DiasVentanaUnAnio = 366;

    // Mismos patrones que RI/SP en calcAdvisor: "reserved instance"/"reserva" para Reserved
    // Instance (inglés y español, la recomendación puede llegar sin curar), "savings plan" para
    // Savings Plan (sin equivalente en español en el catálogo de Advisor).
    private static readonly Regex PatronReserva =
        new("reserved instance|reserva", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PatronSavingsPlan =
        new("savings plan", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Null cuando no hay nada que mostrar en ninguna de las dos mitades del bloque (ni hallazgos
    /// de Advisor ni retiros), igual que <c>calcAdvisor</c> devuelve null con el insumo vacío. En
    /// la plantilla ambas mitades venían del mismo archivo, así que esta condición coincidía con
    /// "el archivo de Advisor no se cargó". En la entrega 2a los retiros salen de
    /// <c>boletin_retirement</c> (módulo Boletín), independiente de <c>waf_resource_finding</c>: un
    /// cliente puede tener retiros pendientes con el backlog de Advisor en cero, y ese caso
    /// legítimamente tiene algo que mostrar aunque la mitad de Advisor salga en cero.
    /// </summary>
    public static PosturaModelo? Calcular(
        IReadOnlyList<AdvisorFila> advisor, IReadOnlyList<RetiroFila> retiros, ContextoInformeValor contexto)
    {
        if (advisor.Count == 0 && retiros.Count == 0) return null;

        var (bruto, real, lineas, porSub) = CalcularAhorro(advisor);
        var (top, topSuma) = CalcularTop(advisor);
        var (retirosCalculados, vencidos, proximos) = CalcularRetiros(retiros, contexto.Corte);

        return new PosturaModelo(
            Total: advisor.Count,
            TiposDeRecomendacion: advisor.Select(a => a.Recomendacion).Distinct().Count(),
            Pilares: CalcularPilares(advisor),
            Suscripciones: Agrupar(advisor.Select(a => a.SubscriptionName)),
            TiposRecurso: AgruparTiposRecurso(advisor),
            Top: top,
            TopSuma: topSuma,
            Detalle: CalcularDetalle(advisor),
            NumRecursos: CalcularNumRecursos(advisor),
            Alto: advisor.Count(a => a.ImpactNumber == 1),
            Medio: advisor.Count(a => a.ImpactNumber == 2),
            Bajo: advisor.Count(a => a.ImpactNumber == 3),
            AhorroBruto: bruto,
            AhorroRealizable: real,
            AhorroDescartado: bruto - real,
            ConAhorroCuantificado: advisor.Count(a => a.AhorroAnual > 0m),
            LineasAhorro: lineas,
            CompromisoPorSuscripcion: porSub,
            Retiros: retirosCalculados,
            RetirosVencidos: vencidos,
            RetirosProximosATresMeses: proximos);
    }

    /// <summary>
    /// <c>cats</c> de <c>calcAdvisor</c>, agrupado por <see cref="AdvisorFila.PillarNumber"/> (D8:
    /// nunca por el texto de <see cref="AdvisorFila.Pilar"/>). El desglose de impacto por pilar usa
    /// <see cref="AdvisorFila.ImpactNumber"/> por el mismo motivo: comparar contra el texto de
    /// <see cref="AdvisorFila.Impacto"/> es exactamente el defecto que deja los tres contadores en
    /// cero frente a un export en español. Orden estable (LINQ <c>OrderByDescending</c> lo es):
    /// un empate en <see cref="PosturaPilar.Cantidad"/> preserva el orden de primera aparición, que
    /// sigue el <c>ORDER BY pillar_number</c> del recolector.
    /// </summary>
    private static List<PosturaPilar> CalcularPilares(IReadOnlyList<AdvisorFila> advisor)
    {
        var vistos = new List<int>();
        var acumulado = new Dictionary<int, (string Nombre, int Cantidad, int Alto, int Medio, int Bajo)>();

        foreach (var fila in advisor)
        {
            if (!acumulado.TryGetValue(fila.PillarNumber, out var actual))
            {
                vistos.Add(fila.PillarNumber);
                actual = (fila.Pilar, 0, 0, 0, 0);
            }

            acumulado[fila.PillarNumber] = (
                actual.Nombre,
                actual.Cantidad + 1,
                actual.Alto + (fila.ImpactNumber == 1 ? 1 : 0),
                actual.Medio + (fila.ImpactNumber == 2 ? 1 : 0),
                actual.Bajo + (fila.ImpactNumber == 3 ? 1 : 0));
        }

        return vistos
            .Select(p => acumulado[p])
            .OrderByDescending(p => p.Cantidad)
            .Select(p => new PosturaPilar(p.Nombre, p.Cantidad, p.Alto, p.Medio, p.Bajo))
            .ToList();
    }

    /// <summary>
    /// Agrupación genérica [nombre, cantidad] ordenada de mayor a menor cantidad (<c>subs</c> y la
    /// base de <c>tipos</c> de <c>calcAdvisor</c>, sin el desglose h/m/l que sí lleva <c>cats</c>).
    /// Nunca produce una clave vacía porque <see cref="AdvisorFila.SubscriptionName"/> ya llega
    /// con el sentinela "(sin suscripción)" del recolector, y <see cref="AgruparTiposRecurso"/>
    /// filtra los tipos vacíos antes de llamar acá.
    /// </summary>
    private static List<IReadOnlyList<object?>> Agrupar(IEnumerable<string> claves)
    {
        var vistos = new List<string>();
        var conteos = new Dictionary<string, int>();
        foreach (var clave in claves)
        {
            if (!conteos.ContainsKey(clave)) vistos.Add(clave);
            conteos[clave] = conteos.GetValueOrDefault(clave) + 1;
        }

        return vistos
            .OrderByDescending(k => conteos[k])
            .Select(k => (IReadOnlyList<object?>)new object?[] { k, conteos[k] })
            .ToList();
    }

    /// <summary>
    /// <c>tipos</c> de <c>calcAdvisor</c>: igual que <see cref="Agrupar"/>, más el balde "Otros
    /// tipos" cuando hay más de 14 tipos distintos (el gráfico de barras no tiene espacio para
    /// más). <see cref="AdvisorFila.ResourceType"/> es <c>NOT NULL</c> en <c>waf_resource_finding</c>,
    /// pero se filtra por si alguna vez llega vacío, igual que <c>.filter(Boolean)</c> en la
    /// plantilla (paridad, no una decisión: nunca se observó un caso real).
    /// </summary>
    private static List<IReadOnlyList<object?>> AgruparTiposRecurso(IReadOnlyList<AdvisorFila> advisor)
    {
        var agrupado = Agrupar(advisor.Select(a => a.ResourceType).Where(t => !string.IsNullOrWhiteSpace(t)));
        if (agrupado.Count <= MaximoTiposRecurso) return agrupado;

        var primeros = agrupado.Take(MaximoTiposRecurso).ToList();
        var otros = agrupado.Skip(MaximoTiposRecurso).Sum(fila => (int)fila[1]!);
        primeros.Add(new object?[] { OtrosTipos, otros });
        return primeros;
    }

    /// <summary>
    /// Trunca igual que <c>calcAdvisor</c> (<c>r.length&gt;105?r.slice(0,102)+'...':r</c>), para
    /// <see cref="CalcularTop"/> y <see cref="CalcularDetalle"/> únicamente: <c>savLineas</c> en la
    /// plantilla NUNCA trunca la recomendación, así que <see cref="PosturaLineaAhorro.Recomendacion"/>
    /// se deja tal cual (ver <see cref="CalcularAhorro"/>).
    /// </summary>
    private static string TruncarRecomendacion(string texto) =>
        texto.Length > LargoMaximoRecomendacion ? texto[..LargoCorteRecomendacion] + "..." : texto;

    /// <summary>
    /// <c>top</c>/<c>topSum</c> de <c>calcAdvisor</c>: agrupa TODAS las filas por
    /// <see cref="AdvisorFila.Recomendacion"/> (sin deduplicar por recurso: cada fila ya es una
    /// recomendación × recurso), toma pilar/impacto de la primera fila del grupo —ya son las
    /// etiquetas correctas derivadas del número (D8), no hace falta resolverlas de nuevo— y se
    /// queda con las 15 de mayor conteo.
    /// </summary>
    private static (List<IReadOnlyList<object?>> top, int topSuma) CalcularTop(IReadOnlyList<AdvisorFila> advisor)
    {
        var vistos = new List<string>();
        var grupos = new Dictionary<string, List<AdvisorFila>>();
        foreach (var fila in advisor)
        {
            if (!grupos.TryGetValue(fila.Recomendacion, out var lista))
            {
                lista = [];
                grupos[fila.Recomendacion] = lista;
                vistos.Add(fila.Recomendacion);
            }
            lista.Add(fila);
        }

        var todas = vistos
            .Select(rec =>
            {
                var g = grupos[rec];
                var cantidad = g.Count;
                var fila = (IReadOnlyList<object?>)new object?[]
                    { TruncarRecomendacion(rec), g[0].Pilar, g[0].Impacto, cantidad };
                return (Fila: fila, Cantidad: cantidad);
            })
            .OrderByDescending(x => x.Cantidad)
            .ToList();

        var top = todas.Take(MaximoTop).Select(x => x.Fila).ToList();
        var topSuma = todas.Take(MaximoTop).Sum(x => x.Cantidad);
        return (top, topSuma);
    }

    /// <summary><c>det</c> de <c>calcAdvisor</c>: igual que <see cref="CalcularTop"/> pero
    /// agrupando por (recomendación, suscripción), sin límite de filas (la plantilla tampoco lo
    /// tiene: solo ordena).</summary>
    private static List<IReadOnlyList<object?>> CalcularDetalle(IReadOnlyList<AdvisorFila> advisor)
    {
        var vistos = new List<(string Rec, string Sub)>();
        var grupos = new Dictionary<(string Rec, string Sub), List<AdvisorFila>>();
        foreach (var fila in advisor)
        {
            var clave = (fila.Recomendacion, fila.SubscriptionName);
            if (!grupos.TryGetValue(clave, out var lista))
            {
                lista = [];
                grupos[clave] = lista;
                vistos.Add(clave);
            }
            lista.Add(fila);
        }

        return vistos
            .Select(clave =>
            {
                var g = grupos[clave];
                return (Fila: (IReadOnlyList<object?>)new object?[]
                    { TruncarRecomendacion(clave.Rec), g[0].Pilar, g[0].Impacto, clave.Sub, g.Count },
                    Cantidad: g.Count);
            })
            .OrderByDescending(x => x.Cantidad)
            .Select(x => x.Fila)
            .ToList();
    }

    /// <summary>
    /// D11: identidad de un recurso = suscripción + grupo de recursos + nombre (la misma terna que
    /// facturación), en vez del nombre global de <see cref="AdvisorFila.ResourceName"/> a solas.
    /// Dos recursos "vm1" en suscripciones o grupos distintos ya no colisionan. Restringido a filas
    /// con nombre de recurso no vacío (ni la plantilla ni esta calculadora pueden atribuir una fila
    /// sin recurso a NINGÚN recurso concreto): mismo filtro que ya existía, ahora sobre la terna
    /// completa en vez de sobre el nombre a solas.
    ///
    /// <para>Limitación que queda documentada en el informe de la Tarea 6, no resuelta acá: D11
    /// también pide que el NUMERADOR de "cada recurso acumula X recomendaciones en promedio" (fuera
    /// de este contrato: esa frase se compone en la capa de dibujo a partir de <c>Total</c> y este
    /// número) se restrinja a filas con recurso. <see cref="PosturaModelo.Total"/> no puede
    /// restringirse sin romper su coherencia con <see cref="PosturaModelo.Alto"/>+
    /// <see cref="PosturaModelo.Medio"/>+<see cref="PosturaModelo.Bajo"/>, que sí cuentan todas las
    /// filas.</para>
    /// </summary>
    private static int CalcularNumRecursos(IReadOnlyList<AdvisorFila> advisor)
    {
        var recursos = new HashSet<(string Suscripcion, string Grupo, string Nombre)>();
        foreach (var fila in advisor)
        {
            if (string.IsNullOrWhiteSpace(fila.ResourceName)) continue;
            recursos.Add((fila.SubscriptionName, fila.ResourceGroup, fila.ResourceName));
        }
        return recursos.Count;
    }

    /// <summary>
    /// D7 completo: deduplica por la identidad de recurso (D11) + recomendación + monto —la misma
    /// fila de Advisor repetida en <c>additional_info</c> bajo más de una forma no se cuenta dos
    /// veces—, y decide el veredicto por línea (<see cref="PosturaLineaAhorro.Contada"/>) con la
    /// SUSCRIPCIÓN como unidad, no la línea: para reserva/savings plan de una suscripción se
    /// cuenta el tipo con la suma mayor completa, nunca los dos ni una línea aislada contra la suma.
    /// Empate exacto: gana reserva (elección arbitraria pero determinística, documentada en el
    /// informe de la Tarea 6 porque la plantilla, en ese caso puntual, marca ambas líneas como
    /// contadas y duplica el total visualmente).
    ///
    /// <para><see cref="PosturaModelo.AhorroBruto"/> es la suma SIN deduplicar (para explicarla
    /// aparte, con la diferencia nombrada en <see cref="PosturaModelo.AhorroDescartado"/>);
    /// <see cref="PosturaModelo.AhorroRealizable"/> es exactamente la suma de las líneas con
    /// <see cref="PosturaLineaAhorro.Contada"/> en true, así que las filas visibles suman el total
    /// impreso (la regla de D7).</para>
    /// </summary>
    private static (
        decimal bruto,
        decimal real,
        List<PosturaLineaAhorro> lineas,
        Dictionary<string, PosturaCompromisoSuscripcion> porSub)
        CalcularAhorro(IReadOnlyList<AdvisorFila> advisor)
    {
        var conAhorro = advisor.Where(a => a.AhorroAnual > 0m).ToList();
        var bruto = conAhorro.Sum(a => a.AhorroAnual!.Value);

        var vistos = new HashSet<(string Sub, string Grupo, string Nombre, string Rec, decimal Monto)>();
        var dedup = new List<AdvisorFila>();
        foreach (var fila in conAhorro)
        {
            var clave = (fila.SubscriptionName, fila.ResourceGroup, fila.ResourceName ?? "",
                fila.Recomendacion, fila.AhorroAnual!.Value);
            if (vistos.Add(clave)) dedup.Add(fila);
        }

        var tipos = dedup.Select(f => ClasificarTipo(f.Recomendacion)).ToList();

        // Reserva y savings plan no se suman entre si: se acumula cada tipo por suscripcion y mas
        // abajo se toma el maximo, porque no se pueden comprar los dos sobre el mismo computo.
        var sumasRi = new Dictionary<string, decimal>();
        var sumasSp = new Dictionary<string, decimal>();
        for (var i = 0; i < dedup.Count; i++)
        {
            if (tipos[i] == TipoReserva)
                sumasRi[dedup[i].SubscriptionName] = sumasRi.GetValueOrDefault(dedup[i].SubscriptionName) + dedup[i].AhorroAnual!.Value;
            else if (tipos[i] == TipoSavingsPlan)
                sumasSp[dedup[i].SubscriptionName] = sumasSp.GetValueOrDefault(dedup[i].SubscriptionName) + dedup[i].AhorroAnual!.Value;
        }

        var porSub = new Dictionary<string, PosturaCompromisoSuscripcion>();
        var compromiso = 0m;
        foreach (var sub in sumasRi.Keys.Union(sumasSp.Keys))
        {
            var ri = sumasRi.GetValueOrDefault(sub);
            var sp = sumasSp.GetValueOrDefault(sub);
            porSub[sub] = new PosturaCompromisoSuscripcion(Reserva: ri, SavingsPlan: sp);
            compromiso += Math.Max(ri, sp);
        }

        var otros = 0m;
        var lineas = new List<PosturaLineaAhorro>();
        for (var i = 0; i < dedup.Count; i++)
        {
            var fila = dedup[i];
            var tipo = tipos[i];
            var monto = fila.AhorroAnual!.Value;
            bool contada;

            if (tipo == TipoOtro)
            {
                contada = true;
                otros += monto;
            }
            else
            {
                var ganaReserva = sumasRi.GetValueOrDefault(fila.SubscriptionName) >= sumasSp.GetValueOrDefault(fila.SubscriptionName);
                contada = tipo == TipoReserva ? ganaReserva : !ganaReserva;
            }

            lineas.Add(new PosturaLineaAhorro(fila.Recomendacion, fila.SubscriptionName, monto, tipo, contada));
        }

        var real = otros + compromiso;
        var ordenadas = lineas.OrderByDescending(l => l.Monto).ToList();
        return (bruto, real, ordenadas, porSub);
    }

    private static string ClasificarTipo(string recomendacion) =>
        PatronReserva.IsMatch(recomendacion) ? TipoReserva
        : PatronSavingsPlan.IsMatch(recomendacion) ? TipoSavingsPlan
        : TipoOtro;

    /// <summary>
    /// D13 (fechas ambiguas), aplicado a <c>rets</c> de <c>calcAdvisor</c>: cada
    /// <see cref="RetiroFila"/> ya llega agrupado por anuncio desde el recolector (no hace falta
    /// re-agrupar acá) y con <see cref="RetiroFila.FechaRetiro"/> tipado como
    /// <see cref="DateOnly"/>? —no como texto de un export en formato de Estados Unidos—, así que
    /// <see cref="Fechas.TryParseFormatoEeuu"/> no hace falta en este camino (queda documentado en
    /// el informe de la Tarea 6). La clasificación compara contra <paramref name="corte"/>, nunca
    /// contra el reloj del sistema.
    /// </summary>
    private static (List<PosturaRetiro> items, int vencidos, int proximos) CalcularRetiros(
        IReadOnlyList<RetiroFila> retiros, DateOnly corte)
    {
        var items = retiros
            // Sin fecha ordena primero, igual que "ts=0" en la plantilla (Date.now() siempre es
            // mayor que 0, así que un retiro sin fecha declarada quedaba primero también ahí).
            .OrderBy(r => r.FechaRetiro?.DayNumber ?? 0)
            .Select(r => ClasificarRetiro(r, corte))
            .ToList();

        return (items, items.Count(r => r.Vencido), items.Count(r => r.ProximoATresMeses));
    }

    private static PosturaRetiro ClasificarRetiro(RetiroFila fila, DateOnly corte)
    {
        var caracteristica = string.IsNullOrWhiteSpace(fila.Caracteristica)
            ? "(sin característica declarada)" : fila.Caracteristica;

        if (fila.FechaRetiro is not { } fecha)
            return new PosturaRetiro(caracteristica, null, fila.RecursosAfectados, "Sin fecha declarada.", false, false);

        // DateOnly vs DateOnly, nunca instante vs instante: la plantilla compara una marca de
        // tiempo UTC-medianoche contra Date.now() (el instante actual), así que un retiro fechado
        // "hoy" sale vencido casi siempre (la medianoche ya pasó). Acá "hoy" es el corte, una
        // FECHA sin hora, y un retiro fechado exactamente en el corte todavía no venció (dias=0
        // cae en la ventana de "menos de tres meses", no en vencido). Divergencia documentada en
        // el informe de la Tarea 6.
        var dias = fecha.DayNumber - corte.DayNumber;
        var fechaTexto = fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (dias < 0)
            return new PosturaRetiro(caracteristica, fechaTexto, fila.RecursosAfectados,
                "VENCIDO. La fecha de retiro ya pasó.", true, false);
        if (dias < DiasVentanaTresMeses)
            return new PosturaRetiro(caracteristica, fechaTexto, fila.RecursosAfectados,
                "Menos de tres meses de margen.", false, true);
        if (dias < DiasVentanaUnAnio)
            return new PosturaRetiro(caracteristica, fechaTexto, fila.RecursosAfectados,
                "Menos de un año de margen.", false, false);
        return new PosturaRetiro(caracteristica, fechaTexto, fila.RecursosAfectados,
            "Plazo largo. Se planifica con el ciclo de renovación.", false, false);
    }
}
