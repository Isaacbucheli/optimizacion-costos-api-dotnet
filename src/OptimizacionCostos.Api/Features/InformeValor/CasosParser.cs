using System.Globalization;

namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>
/// Parser del Excel de requerimientos e incidentes de la mesa de servicio.
/// La duración se guarda CRUDA: la heurística de días contra horas se resuelve sobre el
/// conjunto acumulado en el cálculo, no por archivo.
/// </summary>
public static class CasosParser
{
    public const int MaxRows = 100_000;

    private const string ErrorFormatoMesaServicio =
        "El archivo no tiene la forma del export de la mesa de servicio. Deben estar "
        + "las columnas Caso, Fecha de Registro, Categoría y Cumple SLA.";

    public static ParseResult<CasoRow> Parse(Stream stream)
    {
        var rows = new Dictionary<string, CasoRow>(StringComparer.Ordinal);
        var warnings = new List<string>();
        int total = 0, skipped = 0, truncated = 0, fechasMalas = 0;
        string[]? hdr = null;
        int cCaso = -1, cFecha = -1, cEstado = -1, cSla = -1, cDur = -1, cCumple = -1, cCat = -1, cSub = -1, cHor = -1;

        foreach (var row in XlsxRowReader.Read(stream, MaxRows))
        {
            if (hdr is null)
            {
                // Se saltea toda fila con menos de 3 celdas no vacías: son filas decorativas o de
                // título que pueden aparecer antes de la cabecera real (mismo criterio que
                // BitcostParser). La primera fila que supere ese umbral se toma como cabecera,
                // aunque no tenga la forma esperada; si no mapea columnas, cae en el throw de más
                // abajo con el mismo mensaje que el caso "la cabecera nunca aparece" (ver
                // ErrorFormatoMesaServicio): así, un archivo vacío y uno cuya cabecera nunca llega
                // a superar el umbral de 3 celdas le dan al consultor el mismo texto accionable.
                if (row.Count(x => !string.IsNullOrWhiteSpace(x)) < 3) continue;
                hdr = row;
                cCaso = Col(hdr, "caso", "ticket"); cFecha = Col(hdr, "fecha de registro", "fecha");
                cEstado = Col(hdr, "estado"); cSla = Col(hdr, "sla horas", "sla");
                cDur = Col(hdr, "duracion", "tiempo"); cCumple = Col(hdr, "cumple sla", "cumple");
                cCat = Col(hdr, "categoria"); cSub = Col(hdr, "subcategoria"); cHor = Col(hdr, "horario");
                if (cCumple < 0 || cCaso < 0 || cCat < 0 || cFecha < 0)
                    throw new InvalidOperationException(ErrorFormatoMesaServicio);
                continue;
            }

            total++;
            var caso = Get(row, cCaso);
            var estado = Get(row, cEstado);
            if (caso.Length == 0 && estado.Length == 0) { skipped++; continue; }

            var fechaRaw = Get(row, cFecha);
            DateOnly? fecha = TryFecha(fechaRaw, out var f) ? f : null;
            if (fecha is null && fechaRaw.Length > 0) fechasMalas++;

            var hash = NaturalKey.Hash(caso, fechaRaw, Get(row, cCat), Get(row, cSub), Get(row, cDur));
            rows[hash] = new CasoRow(
                hash,
                Trunc(caso, 120, ref truncated),
                fecha,
                Trunc(estado, 120, ref truncated),
                TryDecimal(Get(row, cSla), out var sla) ? sla : null,
                TryDecimal(Get(row, cDur), out var dur) ? dur : null,
                Trunc(Get(row, cCumple), 20, ref truncated),
                Trunc(Get(row, cCat), 200, ref truncated),
                Trunc(Get(row, cSub), 300, ref truncated),
                Trunc(Get(row, cHor), 120, ref truncated));
        }

        if (hdr is null) throw new InvalidOperationException(ErrorFormatoMesaServicio);
        if (fechasMalas > 0) warnings.Add($"{fechasMalas} casos tienen una fecha de registro que no se pudo leer.");
        if (truncated > 0) warnings.Add($"{truncated} valores se recortaron por exceder el largo de su columna.");

        return new ParseResult<CasoRow>(rows.Values.ToList(), total, skipped, truncated, warnings);
    }

    private static int Col(string[] hdr, params string[] alternativas)
    {
        foreach (var alt in alternativas)
            for (var i = 0; i < hdr.Length; i++)
                if (BitcostParser.Norm(hdr[i]) == alt) return i;
        return -1;
    }

    /// <summary>
    /// Las filas del lector tienen largo variable: los huecos del final no se rellenan hasta el
    /// ancho de la cabecera (XlsxRowReader solo reserva hasta la última celda no vacía de esa
    /// fila). Un índice fuera de rango es, por lo tanto, una celda vacía legítima, no un error.
    /// </summary>
    private static string Get(string[] row, int idx) =>
        idx >= 0 && idx < row.Length ? row[idx].Trim() : string.Empty;

    /// <summary>Acepta ISO, formato local y el serial numérico de Excel (base 1899-12-30).</summary>
    private static bool TryFecha(string raw, out DateOnly value)
    {
        value = default;
        if (raw.Length == 0) return false;
        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)) return true;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial is > 20000 and < 80000)
        {
            value = DateOnly.FromDateTime(new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Utc).AddDays(serial));
            return true;
        }
        return false;
    }

    private static bool TryDecimal(string raw, out decimal value) =>
        decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Recorta al ancho de la columna. El hash ya se calculó sobre el valor completo.</summary>
    private static string? Trunc(string s, int max, ref int counter)
    {
        if (s.Length == 0) return null;
        if (s.Length <= max) return s;
        counter++;
        return s[..max];
    }
}
