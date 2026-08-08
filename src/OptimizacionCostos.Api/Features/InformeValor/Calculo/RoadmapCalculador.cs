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
/// <see cref="RoadmapItem.Esfuerzo"/> queda en cero hasta que exista la columna real, y
/// <see cref="RoadmapModelo.HorasPendientes"/> hereda ese cero a través de la MISMA fórmula de
/// siempre (para que arranque a funcionar solo en cuanto la columna exista, sin tocar este
/// archivo).</para>
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
                Esfuerzo: 0m, // ver el docstring de la clase: brecha de dato, no un calculo pendiente
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
            HorasPendientes: items.Where(i => i.AvancePct <= 0).Sum(i => i.Esfuerzo));
    }

    private static string Recortar(string texto, int max) => texto.Length <= max ? texto : texto[..max];
}
