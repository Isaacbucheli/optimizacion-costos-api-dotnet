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

    /// <summary>
    /// Hermano de BuildXlsx para el caso que ese helper no puede generar: celdas sin atributo
    /// CellReference. BuildXlsx siempre calcula la referencia a partir de la posición en el
    /// arreglo, así que acá cada celda declara la suya explícitamente (o null para omitir el
    /// atributo "r", algo que Excel y otros exportadores sí producen).
    /// </summary>
    private static MemoryStream BuildXlsxConCeldas(IEnumerable<(string? Reference, string Texto)[]> rows, string sheetName = "Export")
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
                foreach (var (reference, texto) in row)
                {
                    var cell = new Cell
                    {
                        DataType = CellValues.InlineString,
                        InlineString = new InlineString(new Text(texto)),
                    };
                    if (reference is not null) cell.CellReference = reference;
                    xr.Append(cell);
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

    /// <summary>
    /// BuildXlsx genera nombres de columna de dos letras en cuanto la fila supera 26 celdas
    /// (columna 27 = "AA", índice 26). Sin este caso, un error de signo o de base en
    /// ColumnIndex para referencias multiletra pasaría inadvertido.
    /// </summary>
    [Fact]
    public void Ubica_una_celda_en_columna_de_dos_letras()
    {
        var fila = Enumerable.Range(0, 27).Select(i => (string?)$"v{i}").ToArray();
        using var xlsx = BuildXlsx([fila]);
        var rows = XlsxRowReader.Read(xlsx, 100).ToList();
        Assert.Equal(27, rows[0].Length);
        Assert.Equal("v26", rows[0][26]); // columna AA
    }

    /// <summary>
    /// Cuando una celda omite el atributo CellReference, OOXML la ubica en la posición
    /// siguiente a la celda anterior de la misma fila (acá, la "b" entre "A1" y "C1" debe
    /// caer en la columna B). Antes de este fix, la celda sin referencia se mandaba a
    /// int.MaxValue-1 y Cells() reservaba un arreglo de ~2^31 posiciones: revienta el proceso
    /// por falta de memoria en vez de ubicar la celda. BuildXlsx no sirve para este caso
    /// porque siempre escribe la referencia; se usa el helper hermano que la deja opcional.
    /// </summary>
    [Fact]
    public void Celda_sin_referencia_usa_la_posicion_siguiente_a_la_anterior()
    {
        using var xlsx = BuildXlsxConCeldas([[("A1", "a"), (null, "b"), ("C1", "c")]]);
        var rows = XlsxRowReader.Read(xlsx, 100).ToList();
        Assert.Equal(["a", "b", "c"], rows[0]);
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
