using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

public sealed class XlsxRowReaderTests
{
    /// <summary>Construye un .xlsx en memoria con las celdas de texto como inlineStr.</summary>
    internal static MemoryStream BuildXlsx(IEnumerable<string?[]> rows, string sheetName = "Export")
    {
        var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook, autoSave: true))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Workbook();
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            var r = 1u;
            foreach (var row in rows)
            {
                var xr = new Row { RowIndex = r };
                for (var c = 0; c < row.Length; c++)
                {
                    if (row[c] is null) continue; // celda ausente: hueco real en el XML
                    xr.Append(new Cell
                    {
                        CellReference = $"{ColName(c)}{r}",
                        DataType = CellValues.InlineString,
                        InlineString = new InlineString(new Text(row[c]!)),
                    });
                }
                data.Append(xr);
                r++;
            }
            wsPart.Worksheet = new Worksheet(data);
            wbPart.Workbook.AppendChild(new Sheets()).AppendChild(
                new Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1u, Name = sheetName });
        }
        ms.Position = 0;
        return ms;
    }

    private static string ColName(int index)
    {
        var s = "";
        for (var i = index; i >= 0; i = i / 26 - 1) s = (char)('A' + i % 26) + s;
        return s;
    }

    /// <summary>
    /// El export real de BITCOST no usa sharedStrings, usa cadenas en línea. Un lector que
    /// solo mire la tabla de cadenas compartidas devuelve las 26 mil filas en blanco sin
    /// lanzar ningún error.
    /// </summary>
    [Fact]
    public void Lee_celdas_con_cadenas_en_linea()
    {
        using var xlsx = BuildXlsx([["Recurso", "PVP"], ["vm-uno", "12.5"]]);
        var rows = XlsxRowReader.Read(xlsx, 100).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(["Recurso", "PVP"], rows[0]);
        Assert.Equal(["vm-uno", "12.5"], rows[1]);
    }

    [Fact]
    public void Alinea_por_posicion_cuando_hay_celdas_ausentes()
    {
        using var xlsx = BuildXlsx([["a", "b", "c"], ["x", null, "z"]]);
        var rows = XlsxRowReader.Read(xlsx, 100).ToList();
        Assert.Equal(["x", "", "z"], rows[1]);
    }

    [Fact]
    public void Superar_el_tope_de_filas_lanza_con_mensaje_para_el_usuario()
    {
        using var xlsx = BuildXlsx([["a"], ["1"], ["2"], ["3"]]);
        var ex = Assert.Throws<InvalidOperationException>(() => XlsxRowReader.Read(xlsx, 2).ToList());
        Assert.Contains("filas", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Un_archivo_que_no_es_xlsx_lanza_con_mensaje_para_el_usuario()
    {
        using var basura = new MemoryStream("no soy un xlsx"u8.ToArray());
        var ex = Assert.Throws<InvalidOperationException>(() => XlsxRowReader.Read(basura, 100).ToList());
        Assert.Contains("Excel", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
