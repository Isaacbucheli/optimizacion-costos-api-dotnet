using System.Globalization;
using System.Text.RegularExpressions;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Bloque de roadmap: matriz WAF. Tarea 7 del plan de la entrega 2b, sin decisión propia de la
/// Parte 1 (a diferencia de los otros cuatro bloques): port fiel de <c>calcMatriz</c> en
/// <c>docs/Plantilla-Dashboard-BIT.html</c> a partir de <see cref="MatrizFila"/>, que ya llega
/// resuelta por el Recolector de la entrega 2a (<see cref="MatrizFila.Ambito"/> ya es la etiqueta
/// de pilar, <see cref="MatrizFila.AvancePct"/> ya es un entero 0-100, no una fracción de Excel que
/// haya que normalizar).
///
/// <para><b>Brecha de datos heredada, no un defecto de este port:</b> <see cref="RoadmapItem.Esfuerzo"/>
/// no tiene fuente numérica todavía. <see cref="MatrizFila.EsfuerzoTexto"/> es texto libre del
/// consultor ("2-3 días", "medio día"): la plantilla original leía un campo YA numérico (horas)
/// de una columna de Excel distinta, y ese numérico no existe en <c>waf_recommendation_tracking</c>
/// (el spec de la entrega ya pide una columna <c>projected_effort_hours DECIMAL(6,2)</c> para
/// esto, fuera de esta entrega 2b). Parsear "2-3 días" con la misma heurística numérica que usa la
/// plantilla (que sencillamente toma el primer número que encuentra) confundiría DÍAS con HORAS y
/// publicaría un número con la unidad equivocada, más engañoso que no publicar nada: por eso
/// <see cref="RoadmapItem.Esfuerzo"/> queda en <c>null</c> ("no medido") hasta que exista la columna
/// real, nunca en <c>0</c> ("no hace falta esfuerzo"). <see cref="RoadmapModelo.HorasPendientes"/>
/// respeta esa misma señal en <see cref="CalcularHorasPendientes"/>: no sustituye el hueco por un
/// cero que parecería una medición.</para>
/// </summary>
public static class RoadmapCalculador
{
    private static readonly Regex PrefijoNumerico = new(@"^[\d.,]+\s*", RegexOptions.Compiled);

    /// <summary>
    /// Sin filtro de período (D0 no aplica a este bloque: la matriz es el backlog vigente al
    /// momento de generar el informe, no una serie temporal — ver el plan de la entrega 2b, tabla
    /// de tareas). Devuelve <c>null</c> cuando no hay ninguna fila con ámbito o hallazgo (insumo
    /// sin datos), igual que <c>calcMatriz</c>.
    /// </summary>
    public static RoadmapModelo? Calcular(IReadOnlyList<MatrizFila> filas)
    {
        var items = new List<RoadmapItem>();
        foreach (var f in filas)
        {
            var ambito = f.Ambito.Trim();
            var hallazgo = f.Hallazgo.Trim();
            if (hallazgo.Length == 0 && ambito.Length == 0) continue; // ver calcMatriz: if(!t&&!a) return;

            items.Add(new RoadmapItem(
                Ambito: ambito.Length == 0 ? "(sin ámbito)" : ambito,
                Hallazgo: Recortar(PrefijoNumerico.Replace(hallazgo, ""), 220),
                Fecha: f.Fecha?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Impacto: f.ImpactNumber ?? 0,
                Prioridad: f.Prioridad,
                Esfuerzo: null, // ver el docstring de la clase: brecha de dato, no un calculo pendiente
                AvancePct: f.AvancePct,
                RecomendacionesAsociadas: f.ResourceCount,
                Registro: f.Registro));
        }
        if (items.Count == 0) return null;

        var ambitos = new List<string>();
        foreach (var item in items)
            if (!ambitos.Contains(item.Ambito))
                ambitos.Add(item.Ambito);

        var amb = ambitos
            .Select(nombre =>
            {
                var deLaMbito = items.Where(i => i.Ambito == nombre).ToList();
                var sumaRecomendaciones = deLaMbito.Sum(i => i.RecomendacionesAsociadas);
                var avgAvance = (double)deLaMbito.Sum(i => i.AvancePct) / deLaMbito.Count;
                return new RoadmapAmbito(
                    Nombre: nombre,
                    Cantidad: deLaMbito.Count,
                    Recomendaciones: sumaRecomendaciones != 0 ? sumaRecomendaciones : deLaMbito.Count,
                    AvancePromedio: (int)Redondeo.ComoJs(avgAvance));
            })
            .OrderByDescending(a => a.Cantidad) // OrderByDescending es estable: empates conservan el orden de aparicion
            .ToList();

        var avancePromedioCrudo = (double)items.Sum(i => i.AvancePct) / items.Count;
        return new RoadmapModelo(
            Total: items.Count,
            Items: items,
            Ambitos: amb,
            Cerrados: items.Count(i => i.AvancePct >= 100),
            EnCurso: items.Count(i => i.AvancePct is > 0 and < 100),
            SinIniciar: items.Count(i => i.AvancePct <= 0),
            AvancePromedio: Redondeo.ComoJs(avancePromedioCrudo * 10) / 10.0,
            HorasPendientes: CalcularHorasPendientes(items));
    }

    /// <summary>
    /// <c>null</c>-safe a propósito (ver el docstring de la clase): una suma parcial que ignora los
    /// ítems sin medir se leería como el total. Tres casos:
    /// <list type="bullet">
    /// <item>Sin ítems sin iniciar: <c>0</c> real (no hay nada pendiente, no es una ausencia).</item>
    /// <item>Con ítems sin iniciar pero al menos uno sin <see cref="RoadmapItem.Esfuerzo"/> medido:
    /// <c>null</c> (hoy siempre cae acá, porque <see cref="Calcular"/> nunca mide ninguno).</item>
    /// <item>Con ítems sin iniciar y TODOS con esfuerzo medido: la suma real. Este camino no lo
    /// ejercita ningún dato de hoy —<see cref="Calcular"/> no tiene de dónde sacar un esfuerzo
    /// medido—, pero la fórmula ya queda lista para cuando exista la columna numérica del spec, sin
    /// tener que tocar este método otra vez.</item>
    /// </list>
    /// </summary>
    private static decimal? CalcularHorasPendientes(List<RoadmapItem> items)
    {
        var sinIniciar = items.Where(i => i.AvancePct <= 0).ToList();
        if (sinIniciar.Count == 0) return 0m;
        if (sinIniciar.Any(i => i.Esfuerzo is null)) return null;
        return sinIniciar.Sum(i => i.Esfuerzo);
    }

    private static string Recortar(string texto, int max) => texto.Length <= max ? texto : texto[..max];
}
