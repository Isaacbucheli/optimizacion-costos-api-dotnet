using System.Globalization;
using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Bloque de consumo: facturación (insumo BITCOST). Tarea 3 del plan de la entrega 2b (D0, D3, D4,
/// D5, D6, D14): port de <c>calcFact</c> en <c>docs/Plantilla-Dashboard-BIT.html</c>, corrigiendo
/// esas seis decisiones en vez de reproducirlas. Ver <see cref="ConsumoModelo"/> para la forma
/// exacta del resultado y el detalle de cada decisión.
///
/// <para><b>D3, en una frase:</b> por categoría, la línea base es la mediana de todos los meses no
/// parciales menos los últimos tres (posicional, no un pico detectado: evita el desempate frágil
/// de "cuál mes fue el máximo" cuando hay una meseta con ruido); el cierre es el promedio de esos
/// últimos tres; la caída sostenida cuenta, hacia atrás desde el último mes, cuántos meses
/// individuales quedan bajo el 60% de la línea base (puede ser 0, puede ser más de 3); y la tasa
/// que se publica es la de la categoría con la mayor caída, pero nunca por encima de la caída NETA
/// de todas las categorías elegibles (si el crecimiento de otras supera la caída elegida, no se
/// publica ningún ahorro).</para>
/// </summary>
public static class ConsumoCalculador
{
    /// <summary>Meses no parciales mínimos para evaluar el ahorro sostenido de una categoría: tres
    /// para la línea base (posicional, ver <see cref="CalcularAhorro"/>) más tres para el cierre.
    /// Mismo mínimo que exigía <c>calcFact</c> (<c>if(v.length&lt;6)return</c>).</summary>
    private const int MinMesesParaAhorro = 6;

    private const decimal UmbralHeuristicaMesParcial = 0.75m;
    private const decimal UmbralCaidaSostenida = 0.6m;

    /// <summary>
    /// D0: <paramref name="filas"/> se restringe al rango de <paramref name="contexto"/> antes de
    /// agrupar nada; <see cref="ConsumoModelo.FilasEnRango"/> publica cuántas quedaron. D14:
    /// <paramref name="filasAntesDeFusionar"/> es <c>rows_processed + rows_merged</c> de la
    /// bitácora de ingesta, de TODA la carga (ver <see cref="ConsumoModelo.Filas"/>): no se
    /// recalcula desde <paramref name="filas"/>, que ya llega fusionada, y no se filtra por rango
    /// porque no se puede (el conteo de fusionadas no está partido por mes). Las dos cifras se
    /// publican juntas, cada una rotulada por lo que cuenta.
    ///
    /// <para>Devuelve <c>null</c> cuando, tras el filtro de rango, no queda ningún mes con datos.
    /// Igual que <c>calcFact</c>, esta calculadora no distingue "no hay insumo cargado" de "el
    /// insumo cargado no se solapa con el rango pedido": las dos producen <c>null</c>.</para>
    /// </summary>
    public static ConsumoModelo? Calcular(
        IReadOnlyList<FacturacionRow> filas, ContextoInformeValor contexto, int filasAntesDeFusionar)
    {
        var enRango = filas.Where(f => EnRango(f.Year, f.Month, contexto.PeriodStart, contexto.PeriodEnd)).ToList();
        if (enRango.Count == 0) return null;

        // Acumuladores. Las listas "orden*" preservan el primer orden de aparición: necesario para
        // desempates estables idénticos a los de la plantilla (Object.keys conserva el orden de
        // inserción en JS al iterar categorías/suscripciones/centros de costo), no un detalle
        // cosmético — ver D3 (empate en la mayor caída) y D0 (comparativa).
        var meses = new Dictionary<string, decimal>();
        var subs = new Dictionary<string, decimal>();
        var ordenSubs = new List<string>();
        var cc = new Dictionary<string, decimal>();
        var ordenCc = new List<string>();
        var rgs = new HashSet<string>();
        var resAll = new HashSet<string>();
        var cats = new Dictionary<string, Dictionary<string, decimal>>();
        var ordenCats = new List<string>();
        var recs = new Dictionary<string, Dictionary<string, decimal>>();

        foreach (var f in enRango)
        {
            var mes = Ym(f.Year, f.Month);
            meses[mes] = meses.GetValueOrDefault(mes) + f.Pvp;

            var sub = string.IsNullOrWhiteSpace(f.SubscriptionName) ? "(sin suscripción)" : f.SubscriptionName!;
            AcumularOrdenado(subs, ordenSubs, sub, f.Pvp);

            var rg = f.ResourceGroup ?? "";
            var res = f.ResourceName ?? "";
            // nRg NO cuenta el balde "" (misma asimetría que calcFact: Object.keys(rgs).filter(Boolean));
            // nRecursos SI lo cuenta si aparece (Object.keys(resAll).length, sin filtro). No es una
            // decisión propia: es la asimetría existente, portada tal cual porque D11 (identidad =
            // suscripción+grupo+nombre) no está entre las decisiones de esta tarea.
            if (rg.Length > 0) rgs.Add(rg);
            resAll.Add(res);

            var cat = string.IsNullOrWhiteSpace(f.Category) ? "(sin categoría)" : f.Category!;
            if (!cats.TryGetValue(cat, out var porMes))
            {
                porMes = [];
                cats[cat] = porMes;
                ordenCats.Add(cat);
            }
            porMes[mes] = porMes.GetValueOrDefault(mes) + f.Pvp;

            var costCenter = string.IsNullOrWhiteSpace(f.CostCenter) ? "(sin asignar)" : f.CostCenter!;
            AcumularOrdenado(cc, ordenCc, costCenter, f.Pvp);

            // D11 (identidad = suscripción+grupo+nombre) no es una decisión de esta tarea, pero
            // calcFact YA arma el id de altas/bajas así: se porta igual, sin cambiarlo.
            var id = (f.SubscriptionId ?? sub) + "|" + rg + "|" + res;
            if (!recs.TryGetValue(id, out var porMesRecurso))
            {
                porMesRecurso = [];
                recs[id] = porMesRecurso;
            }
            porMesRecurso[mes] = porMesRecurso.GetValueOrDefault(mes) + f.Pvp;
        }

        var mk = meses.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

        var autoParciales = DetectarParcialesAutomaticos(mk, meses);
        var (parcial, parcialesInexistentes) = ResolverParciales(contexto.MesesParcialesForzados, autoParciales, mk);

        string? ultCompleto = null;
        for (var i = mk.Count - 1; i >= 0; i--)
        {
            if (parcial.Contains(mk[i])) continue;
            ultCompleto = mk[i];
            break;
        }

        var serieMensual = mk
            .Select(m => (IReadOnlyList<object?>)[m, Redondeo.ComoJs(meses[m]), parcial.Contains(m) ? 1 : 0])
            .ToList();

        var ids = recs.Keys.ToList();
        var serie = ConstruirSerieAltasYBajas(mk, meses, parcial, ids, recs);

        var (bajasDef, cargaRet) = CalcularBajasDefinitivasYCargaRetirada(ids, recs, ultCompleto);

        var prom = CalcularPromediosPorAnio(mk, meses, parcial);

        var ahorro = CalcularAhorro(ordenCats, cats, mk, parcial);

        var comp = CalcularComparativaInteranual(ultCompleto, meses, ordenCats, cats);

        var picoAct = serie.Count == 0 ? 0 : serie.Max(s => (int)s[1]!);
        var mesDePico = serie.Count == 0 ? null : (string)serie.First(s => (int)s[1]! == picoAct)[0]!;

        return new ConsumoModelo(
            Filas: filasAntesDeFusionar,
            FilasEnRango: enRango.Count,
            Total: Redondeo.ComoJs(mk.Sum(m => meses[m])),
            SerieMensual: serieMensual,
            UltimoMesCompleto: ultCompleto,
            MesesParciales: mk.Where(parcial.Contains).ToList(),
            MesesParcialesDetectadosAuto: autoParciales,
            MesesParcialesInexistentes: parcialesInexistentes,
            Suscripciones: ordenSubs
                .OrderByDescending(s => subs[s])
                .Select(s => (IReadOnlyList<object?>)[s, Redondeo.ComoJs(subs[s])])
                .ToList(),
            NumRecursos: resAll.Count,
            NumIdentidades: ids.Count,
            NumGruposRecursos: rgs.Count,
            NumCategorias: cats.Count,
            PicoRecursosActivos: picoAct,
            MesDePicoActivos: mesDePico,
            Serie: serie,
            BajasDefinitivas: bajasDef,
            CargaRetirada: Redondeo.ComoJs(cargaRet),
            UnidadCargaRetirada: "USD, suma del ultimo mes facturado de cada recurso dado de baja",
            PromediosPorAnio: prom,
            Ahorro: ahorro,
            Comparativa: comp,
            PorCentroCosto: ordenCc
                .OrderByDescending(k => cc[k])
                .Select(k => (IReadOnlyList<object?>)[k, Redondeo.ComoJs(cc[k])])
                .ToList());
    }

    private static void AcumularOrdenado(Dictionary<string, decimal> acumulado, List<string> orden, string clave, decimal monto)
    {
        if (!acumulado.ContainsKey(clave)) orden.Add(clave);
        acumulado[clave] = acumulado.GetValueOrDefault(clave) + monto;
    }

    private static bool EnRango(short anio, byte mes, DateOnly inicio, DateOnly fin)
    {
        var clave = anio * 12 + mes;
        var claveInicio = inicio.Year * 12 + inicio.Month;
        var claveFin = fin.Year * 12 + fin.Month;
        return clave >= claveInicio && clave <= claveFin;
    }

    private static string Ym(short anio, byte mes) =>
        anio.ToString("D4", CultureInfo.InvariantCulture) + "-" + mes.ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>
    /// Detección automática de meses parciales: SIEMPRE se calcula (es el diagnóstico que viaja en
    /// <see cref="ConsumoModelo.MesesParcialesDetectadosAuto"/>), independientemente de si
    /// <see cref="ContextoInformeValor.MesesParcialesForzados"/> termina mandando sobre ella o no.
    /// Mismo umbral que <c>calcFact</c>: el mes cae por debajo del 75% de la mediana de los tres
    /// previos, y solo se evalúa para los últimos dos meses del rango ya filtrado por D0.
    /// </summary>
    private static List<string> DetectarParcialesAutomaticos(List<string> mk, Dictionary<string, decimal> meses)
    {
        var auto = new List<string>();
        for (var i = 3; i < mk.Count; i++)
        {
            if (i < mk.Count - 2) continue;
            var tresPrevios = new[] { meses[mk[i - 1]], meses[mk[i - 2]], meses[mk[i - 3]] };
            Array.Sort(tresPrevios);
            var medianaTresPrevios = tresPrevios[1];
            if (meses[mk[i]] < medianaTresPrevios * UmbralHeuristicaMesParcial) auto.Add(mk[i]);
        }
        return auto;
    }

    /// <summary>Tri-estado de <see cref="ContextoInformeValor.MesesParcialesForzados"/>: ver su
    /// propio docstring. Un mes forzado que no existe en <paramref name="mk"/> no se aplica (igual
    /// que <c>calcFact</c>, <c>if(meses[m]!==undefined)</c>) pero SÍ se reporta: a diferencia de la
    /// plantilla, que lo descarta en silencio, acá se devuelve por separado para que
    /// <see cref="ConsumoModelo.MesesParcialesInexistentes"/> lo publique (spec §12.3.3). Solo
    /// aplica cuando <paramref name="forzados"/> trae una lista con elementos: con <c>null</c>
    /// (heurística) o lista vacía (ninguno) no hay nada que el consultor haya declarado mal.
    /// </summary>
    private static (HashSet<string> Parcial, List<string> Inexistentes) ResolverParciales(
        IReadOnlyList<string>? forzados, List<string> auto, List<string> mk)
    {
        if (forzados is null) return ([.. auto], []);
        if (forzados.Count == 0) return ([], []);
        var existentes = mk.ToHashSet();
        var inexistentes = forzados.Where(m => !existentes.Contains(m)).Distinct().ToList();
        return (forzados.Where(existentes.Contains).ToHashSet(), inexistentes);
    }

    /// <summary>
    /// D5: la barra de bajas de un mes parcial no se dibuja (eso es de render(), fuera de esta
    /// tarea), pero el DATO que la alimentaría tiene que llegar en cero: si el mes actual (cur) es
    /// parcial, ni las bajas (conteo) ni el monto retirado de ese mes se calculan — quedan en 0,
    /// no solo excluidas de un agregado posterior. Las altas NO se tocan: D5 solo habla de bajas.
    /// </summary>
    private static List<IReadOnlyList<object?>> ConstruirSerieAltasYBajas(
        List<string> mk, Dictionary<string, decimal> meses, HashSet<string> parcial,
        List<string> ids, Dictionary<string, Dictionary<string, decimal>> recs)
    {
        var serie = new List<IReadOnlyList<object?>>();
        Dictionary<string, decimal>? prev = null;
        foreach (var m in mk)
        {
            var cur = new Dictionary<string, decimal>();
            foreach (var id in ids)
                if (recs[id].TryGetValue(m, out var monto)) cur[id] = monto;

            var alt = 0;
            var baj = 0;
            var pBaj = 0m;
            if (prev is not null)
            {
                foreach (var id in cur.Keys)
                    if (!prev.ContainsKey(id)) alt++;

                if (!parcial.Contains(m)) // D5: mes parcial -> baja y monto retirado quedan en 0
                {
                    foreach (var (id, montoPrevio) in prev)
                        if (!cur.ContainsKey(id)) { baj++; pBaj += montoPrevio; }
                }
            }
            serie.Add([m, cur.Count, alt, baj, Redondeo.ComoJs(meses[m]), Redondeo.ComoJs(pBaj), parcial.Contains(m) ? 1 : 0]);
            prev = cur;
        }
        return serie;
    }

    /// <summary>
    /// D4: una sola cifra de carga retirada — la suma, una vez por recurso, del importe de su
    /// último mes facturado, entre los recursos cuyo último mes es anterior a
    /// <paramref name="ultCompleto"/> (el último mes CERRADO, no el último mes con datos: un
    /// recurso cuyo único mes ausente es el mes parcial final no cuenta acá, D4/D6). Reemplaza a
    /// <c>cargaRet</c> Y a <c>cargaAcum</c> de la plantilla: la segunda no tiene equivalente en
    /// este contrato, desaparece en vez de renombrarse.
    /// </summary>
    private static (int BajasDefinitivas, decimal CargaRetirada) CalcularBajasDefinitivasYCargaRetirada(
        List<string> ids, Dictionary<string, Dictionary<string, decimal>> recs, string? ultCompleto)
    {
        if (ultCompleto is null) return (0, 0m);
        var bajasDef = 0;
        var cargaRet = 0m;
        foreach (var id in ids)
        {
            var ultimoMesDelRecurso = recs[id].Keys.Max(StringComparer.Ordinal);
            if (string.CompareOrdinal(ultimoMesDelRecurso, ultCompleto) < 0)
            {
                bajasDef++;
                cargaRet += recs[id][ultimoMesDelRecurso];
            }
        }
        return (bajasDef, cargaRet);
    }

    private static List<IReadOnlyList<object?>> CalcularPromediosPorAnio(
        List<string> mk, Dictionary<string, decimal> meses, HashSet<string> parcial)
    {
        var anios = new Dictionary<string, List<decimal>>();
        foreach (var m in mk)
        {
            if (parcial.Contains(m)) continue;
            var anio = m[..4];
            if (!anios.TryGetValue(anio, out var lista)) { lista = []; anios[anio] = lista; }
            lista.Add(meses[m]);
        }
        return anios.Keys.OrderBy(x => x, StringComparer.Ordinal)
            .Select(a => (IReadOnlyList<object?>)
                [a, anios[a].Count, Redondeo.ComoJs(anios[a].Average()), Redondeo.ComoJs(anios[a].Sum())])
            .ToList();
    }

    /// <summary>
    /// D3. Por categoría (en <paramref name="ordenCats"/>, orden de primera aparición: desempata
    /// igual que <c>Object.keys</c> en la plantilla), sobre los meses NO parciales:
    /// <list type="bullet">
    /// <item>Línea base = mediana de todos menos los últimos tres (posicional: evita el desempate
    /// frágil de "cuál mes puntual fue el máximo" que tenía la plantilla, ver la nota de la
    /// clase).</item>
    /// <item>Fin = promedio de los últimos tres (los meses cerrados más recientes).</item>
    /// <item>Elegible solo con línea base &gt; 0 y fin &gt;= 0 (bug histórico #1), y solo si
    /// fin &lt; línea base × 0.6 (umbral de caída sostenida, bug histórico #2: una mediana no se
    /// deja engañar por un pico de un solo mes).</item>
    /// <item>Meses sostenido = cuántos meses, contando hacia atrás desde el último, quedan
    /// individualmente bajo ese umbral (puede ser distinto de 3 aun cuando el PROMEDIO de los
    /// últimos tres sí califica: por eso el conteo escanea mes a mes, no reusa el tamaño de la
    /// ventana de "fin").</item>
    /// <item>Gana la categoría con la mayor caída (línea base − fin) entre las elegibles.</item>
    /// <item>La tasa PUBLICADA nunca excede la caída NETA de todas las categorías con historia
    /// suficiente (suban o bajen): si el crecimiento de otras iguala o supera la caída de la
    /// ganadora, no se publica ningún ahorro (bug histórico #3 mencionado en la Parte 1: "el
    /// informe puede titular que el gasto creció... y publicar en la misma sección un ahorro
    /// activo").</item>
    /// <item>Anualizada solo si sostenido &gt;= 3; si no, <c>null</c> explícito (nunca una cifra
    /// implícita).</item>
    /// </list>
    /// </summary>
    private static ConsumoAhorro? CalcularAhorro(
        List<string> ordenCats, Dictionary<string, Dictionary<string, decimal>> cats,
        List<string> mk, HashSet<string> parcial)
    {
        var mesesNoParciales = mk.Where(m => !parcial.Contains(m)).ToList();
        if (mesesNoParciales.Count < MinMesesParaAhorro) return null;

        string? ganadora = null;
        decimal ganadoraDif = 0m, ganadoraLineaBase = 0m, ganadoraFin = 0m;
        var ganadoraSostenido = 0;
        var neto = 0m;

        foreach (var cat in ordenCats)
        {
            var valores = mesesNoParciales.Select(m => cats[cat].GetValueOrDefault(m)).ToList();

            var ventanaBase = valores.Take(valores.Count - 3).ToList(); // todos menos los ultimos 3
            var lineaBase = Mediana(ventanaBase);
            var ventanaFin = valores.Skip(valores.Count - 3); // los ultimos 3 (cerrados)
            var fin = ventanaFin.Average();

            neto += lineaBase - fin; // entra al neteo aunque esta categoria no sea candidata

            if (lineaBase <= 0 || fin < 0) continue; // bug historico #1: base positiva, fin no negativo
            var umbral = lineaBase * UmbralCaidaSostenida;
            if (fin >= umbral) continue; // sin caida sostenida real (bug historico #2)

            var dif = lineaBase - fin;
            if (ganadora is not null && dif <= ganadoraDif) continue;
            ganadora = cat;
            ganadoraDif = dif;
            ganadoraLineaBase = lineaBase;
            ganadoraFin = fin;
            ganadoraSostenido = ContarMesesSostenido(valores, umbral);
        }

        if (ganadora is null) return null;

        var tasaPublicada = Math.Min(ganadoraDif, neto);
        if (tasaPublicada <= 0) return null; // el neteo contra las que subieron anulo la caida elegida

        var tasaRedondeada = Redondeo.ComoJs(tasaPublicada);
        return new ConsumoAhorro(
            Categoria: ganadora,
            LineaBase: Redondeo.ComoJs(ganadoraLineaBase),
            BaseDesdeMes: mesesNoParciales[0],
            Fin: Redondeo.ComoJs(ganadoraFin),
            FinHastaMes: mesesNoParciales[^1],
            TasaMensual: tasaRedondeada,
            MesesSostenido: ganadoraSostenido,
            Anualizada: ganadoraSostenido >= 3 ? Redondeo.ComoJs(tasaRedondeada * 12) : null);
    }

    private static int ContarMesesSostenido(List<decimal> valoresNoParciales, decimal umbral)
    {
        var sostenido = 0;
        for (var i = valoresNoParciales.Count - 1; i >= 0; i--)
        {
            if (valoresNoParciales[i] >= umbral) break;
            sostenido++;
        }
        return sostenido;
    }

    private static decimal Mediana(List<decimal> valores)
    {
        var ordenados = valores.OrderBy(x => x).ToList();
        var n = ordenados.Count;
        return n % 2 == 1 ? ordenados[n / 2] : (ordenados[(n / 2) - 1] + ordenados[n / 2]) / 2m;
    }

    private static ConsumoComparativa? CalcularComparativaInteranual(
        string? ultCompleto, Dictionary<string, decimal> meses,
        List<string> ordenCats, Dictionary<string, Dictionary<string, decimal>> cats)
    {
        if (ultCompleto is null) return null;
        var anioBase = int.Parse(ultCompleto[..4], CultureInfo.InvariantCulture) - 1;
        var mesBase = anioBase.ToString(CultureInfo.InvariantCulture) + ultCompleto[4..];
        if (!meses.ContainsKey(mesBase)) return null; // D0: el mes base tambien tiene que estar en rango

        var filas = ordenCats
            .Select(c => (Cat: c, A: cats[c].GetValueOrDefault(mesBase), B: cats[c].GetValueOrDefault(ultCompleto)))
            .Where(f => f.A > 0 || f.B > 0)
            .OrderByDescending(f => Math.Abs(f.B - f.A))
            .Take(14)
            .Select(f => (IReadOnlyList<object?>)[f.Cat, Redondeo.ComoJs(f.A), Redondeo.ComoJs(f.B)])
            .ToList();
        return new ConsumoComparativa(mesBase, ultCompleto, filas);
    }
}
