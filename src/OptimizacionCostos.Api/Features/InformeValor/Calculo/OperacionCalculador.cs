using System.Text.RegularExpressions;
using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Bloque de operación (Tarea 4 del plan de la entrega 2b): D0, D1, D2 y D10 sobre el insumo
/// "casos" (mesa de servicio), ya persistido como <see cref="CasoRow"/> por la entrega 1
/// (<c>CasosParser</c>). Puerto de <c>calcTickets</c> en <c>docs/Plantilla-Dashboard-BIT.html</c>.
///
/// <para><b>D0.</b> A diferencia de la plantilla (que consume el archivo entero), acá se filtra
/// primero por <see cref="ContextoInformeValor.PeriodStart"/>/<see cref="ContextoInformeValor.PeriodEnd"/>
/// (rango cerrado en los dos extremos) usando <see cref="CasoRow.FechaRegistro"/>. Un caso sin
/// fecha no se puede confirmar dentro del rango, así que queda excluido: no se puede afirmar ni
/// negar su pertenencia, y D1 exige no mezclar un numerador filtrado con un denominador sin
/// filtrar, así que la postura conservadora es no contarlo en ningún lado.</para>
///
/// <para><b>D1.</b> <see cref="OperacionModelo.Categorias"/> y <see cref="OperacionModelo.Frentes"/>
/// agregan un residual explícito ("(sin categoría)"/"(sin subcategoría)") para que la suma de sus
/// elementos dé exactamente <see cref="OperacionModelo.Total"/> (el contrato es explícito: "Total
/// para las dos primeras"). <see cref="OperacionModelo.PorHorario"/> usa la otra rama que permite
/// D1 ("cada agrupación publica su propio denominador"): sigue filtrando los casos sin horario,
/// igual que la plantilla, porque el contrato declara su denominador como "el total de casos CON
/// horario", no <see cref="OperacionModelo.Total"/> — no hace falta un residual ahí.</para>
///
/// <para><b>D2.</b> <see cref="EstadoSla"/> reemplaza los tres criterios contradictorios de la
/// plantilla (KPI exacto, promedio que incluye blancos, tabla que pinta "Sí" todo lo que no es
/// "NO") por una clasificación única, usada en TODOS los cálculos de este bloque: el KPI
/// (<see cref="OperacionModelo.PctCumplimiento"/>, sobre <see cref="OperacionModelo.Cumple"/> +
/// <see cref="OperacionModelo.NoCumple"/>, nunca <see cref="OperacionModelo.Total"/>), el promedio
/// "dentro de SLA" (<see cref="OperacionModelo.MediaHorasDentroSla"/>, solo <c>Cumple</c>) y la
/// etiqueta por fila de <see cref="OperacionModelo.Detalle"/> (tres valores literales, no un
/// binario forzado: <c>ModeloInformeValor</c> ya documenta que la entrega 3 tiene que leer de los
/// tres campos, no de un <c>si</c>/<c>no</c>).</para>
///
/// <para><b>D10.</b> <see cref="OperacionModelo.CasosSinSubcategoria"/> hace auditable la
/// exclusión: esos casos no matchean el regex reactivo (correcto, ya lo hacía la plantilla) pero
/// tampoco pueden caer en "proactivo" por default. El titular/KPI por volumen
/// (<see cref="OperacionModelo.CasosReactivos"/> sobre <see cref="OperacionModelo.Total"/>) y la
/// métrica por frentes (<see cref="OperacionModelo.FrentesReactivos"/> y
/// <see cref="OperacionModelo.FrentesProactivos"/> sobre su suma) quedan las dos disponibles y
/// pueden divergir; cuál se titula es una decisión de <c>render()</c> (entrega 3), no de este
/// cálculo — la regla vigente es que el titular y el KPI usan la de volumen, y la de frentes se
/// publica en el cuerpo rotulada como tal.</para>
///
/// <para>El residual de D1 obligó a publicar <see cref="OperacionModelo.FrentesProactivos"/> como
/// campo propio: "todos los frentes menos los reactivos" lo contaba del lado proactivo, y con un
/// export sin la columna Subcategoría poblada esa resta publicaba 100 % de trabajo proactivo junto
/// al 0,0 % por volumen. Ver el docstring de ese campo.</para>
/// </summary>
public static class OperacionCalculador
{
    private const string SinCategoria = "(sin categoría)";
    private const string SinSubcategoria = "(sin subcategoría)";

    private static readonly Regex Reactivo = new(
        "(fallo|falla|error|caida|caída|no responde|sin respuesta|indisponib|degrad|interrup|" +
        "lentitud|lento|incidente|down|outage)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Cerrado = new(
        "cerrad|closed|resuelt", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PrefijoHorario = new(
        @"^Horario\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private enum EstadoSla { Cumple, NoCumple, SinEvaluar }

    /// <summary>Una fila ya en el rango del período, con la clasificación de SLA (D2) resuelta y
    /// las claves de agrupación (D1) calculadas una sola vez. Los campos "Cruda" son el texto tal
    /// cual llegó (pueden ser vacíos): el detalle por fila (<see cref="OperacionModelo.Detalle"/>/
    /// <see cref="OperacionModelo.FueraDeSla"/>) los usa directo, sin el residual — D1 exige el
    /// residual para AGRUPAR, no para reescribir el dato crudo de cada fila.</summary>
    private sealed record CasoPreparado(
        string Caso, DateOnly Fecha, string? Estado, decimal Sla, decimal Duracion, EstadoSla Clasificacion,
        string? CategoriaCruda, string Categoria, string? SubcategoriaCruda, string Subcategoria, string Horario);

    private sealed record MesResumen(DateOnly Clave, int Total, int FueraDeSla);

    public static OperacionModelo? Calcular(IReadOnlyList<CasoRow> casos, ContextoInformeValor contexto)
    {
        var enRango = casos
            .Where(c => c.FechaRegistro is { } f && f >= contexto.PeriodStart && f <= contexto.PeriodEnd)
            .Select(Preparar)
            .ToList();

        if (enRango.Count == 0) return null;

        // D13/"cinco cuentas": el p90 de la duración CRUDA decide si la mesa reportó en días. El
        // p90 expuesto en el modelo (P90Horas) se recalcula más abajo, ya sobre horas ajustadas.
        var p90Cruda = Percentil([.. enRango.Select(t => t.Duracion)], 0.9);
        var enDias = p90Cruda < 30 && p90Cruda > 0;
        var raw = enDias ? [.. enRango.Select(t => t with { Duracion = t.Duracion * 24 })] : enRango;

        var cumple = raw.Count(t => t.Clasificacion == EstadoSla.Cumple);
        var noCumple = raw.Count(t => t.Clasificacion == EstadoSla.NoCumple);
        var sinEvaluar = raw.Count(t => t.Clasificacion == EstadoSla.SinEvaluar);
        var denominadorPct = cumple + noCumple;

        var duraciones = raw.Select(t => t.Duracion).ToList();
        var duracionesOk = raw.Where(t => t.Clasificacion == EstadoSla.Cumple).Select(t => t.Duracion).ToList();

        var categorias = raw.GroupBy(t => t.Categoria)
            .Select(g => new OperacionCategoria(
                g.Key, g.Count(), g.Count(t => t.Clasificacion == EstadoSla.NoCumple), (double)Mediana([.. g.Select(t => t.Duracion)])))
            .OrderByDescending(c => c.Cantidad)
            .ToList();

        var frentes = raw.GroupBy(t => t.Subcategoria)
            .Select(g => new OperacionFrente(g.Key, g.Count(), g.Key != SinSubcategoria && Reactivo.IsMatch(g.Key)))
            .OrderByDescending(f => f.Cantidad)
            .ToList();
        var casosReactivos = raw.Count(t => t.SubcategoriaCruda is { Length: > 0 } s && Reactivo.IsMatch(s));
        var casosSinSubcategoria = raw.Count(t => string.IsNullOrWhiteSpace(t.SubcategoriaCruda));

        var porHorario = raw.Where(t => t.Horario.Length > 0)
            .GroupBy(t => t.Horario)
            .Select(g => (IReadOnlyList<object?>)[g.Key, g.Count()])
            .OrderByDescending(h => (int)h[1]!)
            .ToList();

        var meses = raw.GroupBy(t => new DateOnly(t.Fecha.Year, t.Fecha.Month, 1))
            .Select(g => new MesResumen(g.Key, g.Count(), g.Count(t => t.Clasificacion == EstadoSla.NoCumple)))
            .OrderBy(m => m.Clave)
            .ToList();
        var (racha, rachaCasos) = Racha(meses);

        var fechas = raw.Select(t => t.Fecha).OrderBy(f => f).ToList();

        var fueraDeSla = raw.Where(t => t.Clasificacion == EstadoSla.NoCumple).Select(FilaSinEtiqueta).ToList();

        var detalle = raw.OrderByDescending(t => t.Fecha)
            .Select(t => (IReadOnlyList<object?>)[
                t.Caso, t.Fecha.ToString("yyyy-MM-dd"), t.CategoriaCruda ?? "", t.SubcategoriaCruda ?? "",
                t.Sla, Redondeo.ComoJs(t.Duracion, 2), Etiqueta(t.Clasificacion), t.Horario])
            .ToList();

        return new OperacionModelo(
            Total: raw.Count, Cumple: cumple, NoCumple: noCumple, SinEvaluar: sinEvaluar,
            PctCumplimiento: Division.Porcentaje(cumple, denominadorPct),
            DenominadorPctCumplimiento: denominadorPct,
            Cerrados: raw.Count(t => t.Estado is { Length: > 0 } e && Cerrado.IsMatch(e)),
            MediaHoras: duraciones.Count == 0 ? 0d : (double)(duraciones.Sum() / duraciones.Count),
            MedianaHoras: (double)Mediana(duraciones),
            P90Horas: (double)Percentil(duraciones, 0.9),
            MediaHorasDentroSla: duracionesOk.Count == 0 ? 0d : (double)(duracionesOk.Sum() / duracionesOk.Count),
            DuracionOriginalEnDias: enDias,
            Categorias: categorias,
            SerieMensual: [.. meses.Select(m => (IReadOnlyList<object?>)[m.Clave.ToString("yyyy-MM"), m.Total, m.FueraDeSla])],
            RachaMesesSinIncumplir: racha, RachaCasos: rachaCasos,
            Frentes: frentes, TotalFrentes: frentes.Count,
            FrentesReactivos: frentes.Count(f => f.EsReactivo),
            // El residual de D1 no es proactivo: no se clasificó. Ver OperacionModelo.FrentesProactivos.
            FrentesProactivos: frentes.Count(f => !f.EsReactivo && f.Nombre != SinSubcategoria),
            CasosReactivos: casosReactivos,
            CasosSinSubcategoria: casosSinSubcategoria,
            PorHorario: porHorario,
            Desde: fechas[0].ToString("yyyy-MM-dd"), Hasta: fechas[^1].ToString("yyyy-MM-dd"),
            FueraDeSla: fueraDeSla,
            Detalle: detalle);
    }

    private static CasoPreparado Preparar(CasoRow c)
    {
        var subcategoriaCruda = c.Subcategoria;
        var horario = PrefijoHorario.Replace(c.Horario ?? "", "").Trim();

        return new CasoPreparado(
            Caso: c.Caso ?? "",
            Fecha: c.FechaRegistro!.Value,
            Estado: c.Estado,
            Sla: c.SlaHoras ?? 0m,
            Duracion: c.DuracionCruda ?? 0m,
            Clasificacion: ClasificarSla(c.Cumple),
            CategoriaCruda: c.Categoria,
            Categoria: string.IsNullOrWhiteSpace(c.Categoria) ? SinCategoria : c.Categoria.Trim(),
            SubcategoriaCruda: subcategoriaCruda,
            Subcategoria: string.IsNullOrWhiteSpace(subcategoriaCruda) ? SinSubcategoria : subcategoriaCruda.Trim(),
            Horario: horario);
    }

    private static EstadoSla ClasificarSla(string? cumpleRaw)
    {
        var norm = (cumpleRaw ?? "").Trim().ToUpperInvariant();
        return norm switch
        {
            "SI" or "SÍ" or "YES" => EstadoSla.Cumple,
            "NO" => EstadoSla.NoCumple,
            _ => EstadoSla.SinEvaluar,
        };
    }

    private static string Etiqueta(EstadoSla estado) => estado switch
    {
        EstadoSla.Cumple => "SI",
        EstadoSla.NoCumple => "NO",
        _ => "SIN EVALUAR",
    };

    private static IReadOnlyList<object?> FilaSinEtiqueta(CasoPreparado t) =>
        [t.Caso, t.Fecha.ToString("yyyy-MM-dd"), t.CategoriaCruda ?? "", t.SubcategoriaCruda ?? "", t.Sla, Redondeo.ComoJs(t.Duracion, 2)];

    private static (int Racha, int RachaCasos) Racha(IReadOnlyList<MesResumen> meses)
    {
        var racha = 0;
        var rachaCasos = 0;
        for (var i = meses.Count - 1; i >= 0; i--)
        {
            if (meses[i].FueraDeSla > 0) break;
            racha++;
            rachaCasos += meses[i].Total;
        }
        return (racha, rachaCasos);
    }

    /// <summary>Nearest-rank, igual que <c>pctl</c> de la plantilla: sin interpolar entre vecinos.</summary>
    private static decimal Percentil(IReadOnlyList<decimal> valores, double p)
    {
        if (valores.Count == 0) return 0m;
        var s = valores.OrderBy(x => x).ToList();
        var idx = Math.Min(s.Count - 1, (int)Math.Floor(s.Count * p));
        return s[idx];
    }

    /// <summary>Igual que <c>med</c> de la plantilla: promedio de los dos centrales si el conteo es par.</summary>
    private static decimal Mediana(IReadOnlyList<decimal> valores)
    {
        if (valores.Count == 0) return 0m;
        var s = valores.OrderBy(x => x).ToList();
        var n = s.Count;
        return n % 2 == 1 ? s[(n - 1) / 2] : (s[n / 2 - 1] + s[n / 2]) / 2m;
    }
}
