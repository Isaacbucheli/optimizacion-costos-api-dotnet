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
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
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
        foreach (var cell in row.Elements<Cell>())
            byIndex[ColumnIndex(cell.CellReference?.Value)] = Text(cell, sst);

        if (byIndex.Count == 0) return [];
        var last = byIndex.Keys.Max();
        var result = new string[last + 1];
        for (var i = 0; i <= last; i++) result[i] = byIndex.TryGetValue(i, out var v) ? v : string.Empty;
        return result;
    }

    /// <summary>"C7" → 2. Sin referencia, cae al final para no pisar una columna real.</summary>
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return int.MaxValue - 1;
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
