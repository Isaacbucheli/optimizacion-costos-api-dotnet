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
    public static IEnumerable<string[]> Read(Stream stream, int maxRows)
    {
        SpreadsheetDocument doc;
        try { doc = SpreadsheetDocument.Open(stream, false); }
        catch (Exception ex) when (ex is FileFormatException or FormatException or ArgumentException)
        {
            // Estas son las excepciones que de verdad significan "esto no es un .xlsx":
            // FileFormatException es la que lanza la librería al abrir un paquete que no es un
            // zip/OOXML válido (incluye el caso de un stream vacío), y FormatException/
            // ArgumentException cubren fallos de parseo o de argumentos inválidos dentro del
            // paquete. Cualquier otra cosa (E/S, memoria, un bug nuestro) se propaga: el
            // controller ya tiene un catch general que responde 500, que es lo correcto para
            // una falla que no es del archivo.
            throw new InvalidOperationException("El archivo no es un Excel (.xlsx) válido.");
        }

        using (doc)
        {
            var wbPart = doc.WorkbookPart
                ?? throw new InvalidOperationException("El archivo no es un Excel (.xlsx) válido.");
            var sheet = wbPart.Workbook.Descendants<Sheet>().FirstOrDefault()
                ?? throw new InvalidOperationException("El Excel no contiene ninguna hoja.");
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
    /// atributo "r" (implícitamente consecutiva a la anterior).
    /// </summary>
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var n = 0;
        foreach (var ch in reference)
        {
            if (ch is < 'A' or > 'Z') break;
            n = n * 26 + (ch - 'A' + 1);
        }
        return n - 1;
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
