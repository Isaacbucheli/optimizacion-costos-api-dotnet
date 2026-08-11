using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>
/// Lector en streaming de la primera hoja de un .xlsx. NO usa ClosedXML a propósito:
/// ClosedXML materializa el libro completo antes de recorrer nada (del orden de 0,5 a 1 KB
/// por celda no vacía) y el App Service es un plan B1 compartido de 1,75 GB. El export de
/// BITCOST medido son 26.611 filas x 15 columnas, 23 MB de XML descomprimido.
/// </summary>
public static class XlsxRowReader
{
    /// <summary>
    /// Nombres de las hojas del libro, en el orden en que aparecen (el mismo orden que ve un
    /// consultor en las pestañas de Excel). Usado por RbacParser para decidir qué hoja leer entre
    /// las nueve del export de Revisión de accesos antes de pagar el costo de recorrer ninguna.
    /// </summary>
    public static IReadOnlyList<string> ReadSheetNames(Stream stream)
    {
        using var doc = AbrirPaquete(stream);
        var wbPart = doc.WorkbookPart
            ?? throw new InvalidOperationException("El archivo no es un Excel (.xlsx) válido.");
        return [.. wbPart.Workbook.Descendants<Sheet>().Select(s => s.Name?.Value ?? string.Empty)];
    }

    /// <summary>
    /// Lee la hoja indicada por nombre (comparación exacta tras recortar espacios), o la primera
    /// del libro si <paramref name="sheetName"/> es null — el comportamiento original, que
    /// BitcostParser y CasosParser siguen usando sin cambios.
    /// </summary>
    public static IEnumerable<string[]> Read(Stream stream, int maxRows, string? sheetName = null)
    {
        using (var doc = AbrirPaquete(stream))
        {
            var wbPart = doc.WorkbookPart
                ?? throw new InvalidOperationException("El archivo no es un Excel (.xlsx) válido.");
            var sheet = sheetName is null
                ? wbPart.Workbook.Descendants<Sheet>().FirstOrDefault()
                    ?? throw new InvalidOperationException("El Excel no contiene ninguna hoja.")
                : wbPart.Workbook.Descendants<Sheet>()
                    .FirstOrDefault(s => string.Equals(s.Name?.Value?.Trim(), sheetName, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"El Excel no contiene una hoja llamada '{sheetName}'.");
            var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
            var sst = wbPart.SharedStringTablePart?.SharedStringTable;

            var count = 0;
            using var reader = OpenXmlReader.Create(wsPart);
            while (reader.Read())
            {
                if (reader.ElementType != typeof(Row)) continue;
                if (reader.LoadCurrentElement() is not Row row) continue;

                if (++count > maxRows)
                    throw new InvalidOperationException(
                        $"El archivo tiene más de {maxRows:N0} filas. Revisa que el export sea del período correcto.");

                yield return Cells(row, sst);
            }
        }
    }

    /// <summary>Abre el paquete OOXML traduciendo los fallos de formato a un mensaje para el
    /// usuario. Compartido por <see cref="Read"/> y <see cref="ReadSheetNames"/>.</summary>
    private static SpreadsheetDocument AbrirPaquete(Stream stream)
    {
        try { return SpreadsheetDocument.Open(stream, false); }
        catch (Exception ex) when (ex is FileFormatException or FormatException or ArgumentException
            or OpenXmlPackageException)
        {
            // Estas son las excepciones que de verdad significan "esto no es un .xlsx":
            // FileFormatException es la que lanza la librería al abrir un paquete que no es un
            // zip/OOXML válido (incluye el caso de un stream vacío), OpenXmlPackageException es
            // la que lanza cuando el paquete OOXML tiene relaciones o content-types inválidos, y
            // FormatException/ArgumentException cubren fallos de parseo o de argumentos
            // inválidos dentro del paquete. Cualquier otra cosa (E/S, memoria, un bug nuestro) se
            // propaga: el controller ya tiene un catch general que responde 500, que es lo
            // correcto para una falla que no es del archivo.
            throw new InvalidOperationException("El archivo no es un Excel (.xlsx) válido.");
        }
    }

    private static string[] Cells(Row row, SharedStringTable? sst)
    {
        var byIndex = new SortedDictionary<int, string>();
        var next = 0; // posición para la próxima celda sin referencia; se reinicia en cada fila
        foreach (var cell in row.Elements<Cell>())
        {
            var index = ColumnIndex(cell.CellReference?.Value) ?? next;
            byIndex[index] = Text(cell, sst);
            next = index + 1;
        }

        if (byIndex.Count == 0) return [];
        var last = byIndex.Keys.Max();
        var result = new string[last + 1];
        for (var i = 0; i <= last; i++) result[i] = byIndex.TryGetValue(i, out var v) ? v : string.Empty;
        return result;
    }

    /// <summary>
    /// "C7" → 2. Sin referencia, null: el llamador (Cells) la ubica en la posición siguiente a
    /// la última celda leída de esa fila, que es como OOXML define la posición de una celda sin
    /// atributo "r" (implícitamente consecutiva a la anterior). Referencias con más de 3 letras,
    /// o cuyo valor cae fuera de las 16.384 columnas de Excel (la última es "XFD"), también
    /// devuelven null y caen en ese mismo camino: sin este límite, una racha larga de letras
    /// desborda el acumulador (aritmética unchecked) y puede volver a producir el arreglo
    /// desmedido que las celdas sin referencia producían antes del fix anterior.
    /// </summary>
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        const int columnasMaximasExcel = 16_384; // "XFD"
        var n = 0;
        var letras = 0;
        foreach (var ch in reference)
        {
            if (ch is < 'A' or > 'Z') break;
            if (++letras > 3) return null; // "XFD" tiene 3 letras; más de 3 no puede ser una columna válida
            n = n * 26 + (ch - 'A' + 1);
        }
        return n is >= 1 and <= columnasMaximasExcel ? n - 1 : null;
    }

    private static string Text(Cell cell, SharedStringTable? sst)
    {
        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.Text?.Text ?? cell.InnerText;

        var raw = cell.CellValue?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString && sst is not null
            && int.TryParse(raw, out var idx) && idx >= 0 && idx < sst.ChildElements.Count)
            return sst.ChildElements[idx].InnerText;

        return raw;
    }
}
