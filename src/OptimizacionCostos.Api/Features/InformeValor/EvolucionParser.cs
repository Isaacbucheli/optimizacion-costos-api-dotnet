using System.Globalization;
using static OptimizacionCostos.Api.Features.InformeValor.InsumoCellUtils;

namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>
/// Parser del export "Evolución de Consumo por Recurso" de BITCOST: un pivot con dos filas de
/// encabezado (años y meses), jerarquía Categoría &gt; Subcategoría &gt; Recurso con fill-down,
/// subtotales intercalados en tres niveles, columnas "Total" entre los meses (la causa del KPI
/// de $26,683 leído de un subtotal) y un pie de filtros con tenant IDs que no se persiste.
///
/// <para><b>Contadores:</b> a diferencia de <see cref="BitcostParser"/>, acá una fila de entrada
/// produce N filas de salida (una por mes con valor). "Row" para los contadores es la CELDA de
/// mes con valor sobre una fila de recurso válida: RowsTotal = Rows + RowsSkipped + RowsMerged
/// se conserva con esa definición. Las filas de subtotal y el pie son estructura del pivot, no
/// datos: no entran en ningún contador (un warning por el pie deja rastro).</para>
/// </summary>
public static class EvolucionParser
{
    public const int MaxRows = 200_000;

    internal const string ErrorFormato =
        "El archivo no tiene el formato del export de evolución por recurso de BITCOST " +
        "(dos filas de encabezado de fechas y columnas Categoría / Subcategoría / Recurso).";

    private static readonly Dictionary<string, byte> MesPorNombre = new()
    {
        ["enero"] = 1, ["febrero"] = 2, ["marzo"] = 3, ["abril"] = 4, ["mayo"] = 5, ["junio"] = 6,
        ["julio"] = 7, ["agosto"] = 8, ["septiembre"] = 9, ["setiembre"] = 9, ["octubre"] = 10,
        ["noviembre"] = 11, ["diciembre"] = 12,
    };

    public static ParseResult<EvolucionRow> Parse(Stream stream)
    {
        string[]? filaAnios = null, filaPrev = null;
        List<(int Col, short Anio, byte Mes)>? columnas = null;
        var acumulado = new Dictionary<string, EvolucionRow>();
        int total = 0, skipped = 0, fusionadas = 0, truncated = 0, pvpInvalido = 0;
        var pieDescartado = false;
        string cat = "", sub = "";

        foreach (var celdas in XlsxRowReader.Read(stream, MaxRows))
        {
            if (columnas is null)
            {
                if (celdas.Length >= 3 &&
                    Norm(Get(celdas, 0)) == "categoria" &&
                    Norm(Get(celdas, 1)) == "subcategoria" &&
                    Norm(Get(celdas, 2)) == "recurso")
                {
                    if (filaPrev is null || filaAnios is null) throw new InvalidOperationException(ErrorFormato);
                    columnas = ClasificarColumnas(filaAnios, filaPrev);
                    if (columnas.Count == 0) throw new InvalidOperationException(ErrorFormato);
                    continue;
                }
                filaAnios = filaPrev;
                filaPrev = celdas;
                continue;
            }

            if (celdas.Length == 0) continue;
            var c0 = Get(celdas, 0);
            if (celdas.Length == 1 && c0.StartsWith("Filtros aplicados", StringComparison.OrdinalIgnoreCase))
            {
                pieDescartado = true; // trae FriendlyName y tenant IDs: jamás se persiste
                continue;
            }

            var c1 = Get(celdas, 1);
            var rec = Get(celdas, 2);

            if (c0.Length > 0)
            {
                if (Norm(c0) == "total") continue;   // gran total del pivot
                cat = c0; sub = "";                  // fill-down: nueva categoría resetea subcategoría
            }
            if (c1.Length > 0)
            {
                if (Norm(c1) == "total") continue;   // subtotal de categoría (recurso vacío)
                sub = c1;
            }
            if (rec.Length == 0) continue;           // fila estructural sin recurso
            if (Norm(rec) == "total") continue;      // subtotal de subcategoría

            var esReserva = rec.StartsWith("Reserved VM Instance", StringComparison.OrdinalIgnoreCase);
            foreach (var (col, anio, mes) in columnas)
            {
                var txt = Get(celdas, col);
                if (txt.Length == 0) continue;       // sin consumo ese mes: no es dato
                total++;
                if (!TryDecimal(txt, out var pvp)) { skipped++; pvpInvalido++; continue; }

                // El hash siempre sobre el valor completo, antes de truncar (regla del módulo:
                // truncar antes de hashear funde dos recursos que difieran después del ancho).
                var key = NaturalKey.Hash(cat, sub, rec,
                    anio.ToString(CultureInfo.InvariantCulture), mes.ToString(CultureInfo.InvariantCulture));
                if (acumulado.TryGetValue(key, out var previa))
                {
                    acumulado[key] = previa with { Pvp = previa.Pvp + pvp };
                    fusionadas++;
                    continue;
                }
                acumulado[key] = new EvolucionRow(
                    key,
                    Trunc(cat, 200, ref truncated),
                    Trunc(sub, 200, ref truncated),
                    Trunc(rec, 512, ref truncated)!,
                    esReserva, pvp, anio, mes);
            }
        }

        if (columnas is null) throw new InvalidOperationException(ErrorFormato);

        var warnings = new List<string>();
        if (fusionadas > 0) warnings.Add($"{fusionadas} celdas colapsaron a una clave ya vista y se sumaron.");
        if (pvpInvalido > 0) warnings.Add($"{pvpInvalido} celdas de PvP no se pudieron convertir y quedaron fuera.");
        if (truncated > 0) warnings.Add($"{truncated} valores superaron el ancho de columna y se truncaron.");
        if (pieDescartado) warnings.Add("Se descartó el pie de filtros del export (no se persiste).");

        return new ParseResult<EvolucionRow>(acumulado.Values.ToList(), total, skipped, fusionadas, truncated, warnings);
    }

    /// <summary>Una columna es de mes si la fila de meses trae un nombre de mes en español
    /// (con o sin espacio adelante). Las columnas "Total" y vacías quedan fuera acá: es la
    /// guarda contra leer un subtotal como si fuera un mes. El año se arrastra hacia la derecha
    /// porque un pivot con celdas combinadas lo trae solo en la primera columna del bloque.</summary>
    internal static List<(int Col, short Anio, byte Mes)> ClasificarColumnas(string[] filaAnios, string[] filaMeses)
    {
        var columnas = new List<(int, short, byte)>();
        short anioActual = 0;
        var ancho = Math.Max(filaAnios.Length, filaMeses.Length);
        for (var i = 3; i < ancho; i++)
        {
            var anioTxt = Get(filaAnios, i);
            if (short.TryParse(anioTxt, out var a) && a is >= 2000 and <= 2100) anioActual = a;
            if (MesPorNombre.TryGetValue(Norm(Get(filaMeses, i)), out var mes) && anioActual > 0)
                columnas.Add((i, anioActual, mes));
        }
        return columnas;
    }
}
