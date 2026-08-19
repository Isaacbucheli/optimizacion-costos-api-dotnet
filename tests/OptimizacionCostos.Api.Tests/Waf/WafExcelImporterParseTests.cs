using System.Text;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Contratos del parseo del Excel de matriz WAF (hallazgos UPL-01/UPL-02 del informe
/// BIT-TEST-DAST v1.2). Parse no toca BD ni IA: el importer se construye con esos
/// puertos en null/fake a propósito.
/// </summary>
public sealed class WafExcelImporterParseTests
{
    private sealed class NoChat : IChatCompletionClient
    {
        public string Complete(string system, string userJson, int maxCompletionTokens = 500) => "";
    }

    private static ClosedXmlWafImporter NewImporter() =>
        new(factory: null!, catalog: null!, chat: new NoChat(), config: new AppConfig());

    /// <summary>UPL-02 (DAST): un contenido no-Excel renombrado .xlsx explotaba como
    /// FileFormatException → 500. Debe ser un rechazo de usuario (400 vía
    /// InvalidOperationException), igual que XlsxRowReader.AbrirPaquete en InformeValor.</summary>
    [Fact]
    public void Un_archivo_que_no_es_xlsx_lanza_con_mensaje_para_el_usuario()
    {
        var basura = Encoding.UTF8.GetBytes("esto no es un zip OOXML");

        var ex = Assert.Throws<InvalidOperationException>(() => NewImporter().Parse(basura));

        Assert.Contains("Excel", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildXlsx(string sheetName, params string[][] rows)
    {
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.Worksheets.Add(sheetName);
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                ws.Cell(r + 1, c + 1).Value = rows[r][c];
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>UPL-01 (DAST): el caso literal del pentest — un Excel válido con contenido
    /// (marcador HTML inerte, texto, fecha) pero sin la fila de encabezado de la matriz
    /// respondía rows_total=0 sin advertir nada. Ahora lo declara.</summary>
    [Fact]
    public void Sin_encabezado_lo_declara_y_cuenta_todo_el_contenido_como_descartado()
    {
        var xlsx = BuildXlsx("Resultados",
            ["<b>hola</b>", "texto", "2026-08-12"]);

        var (rows, metrics) = NewImporter().Parse(xlsx);

        Assert.Empty(rows);
        Assert.False(metrics.HeaderFound);
        Assert.Equal(1, metrics.RowsSkipped);
        Assert.NotEmpty(metrics.Warnings);
    }

    [Fact]
    public void Con_encabezado_las_filas_invalidas_se_cuentan_y_advierten()
    {
        var xlsx = BuildXlsx("Resultados",
            ["Titulo del documento"],                              // pre-header legítimo: NO cuenta
            ["Ámbito de revisión", "Descripción", "Recursos"],     // header
            ["1 Optimizacion de costos"],                          // sección de pilar: NO cuenta
            ["1.1 Recomendación válida", "desc", "vm-01"],         // válida
            ["sin código ni forma"],                               // descartada (sin tracking)
            [""],                                                  // vacía: NO cuenta
            ["otra suelta sin forma"]);                            // descartada

        var (rows, metrics) = NewImporter().Parse(xlsx);

        Assert.Single(rows);
        Assert.True(metrics.HeaderFound);
        Assert.Equal(2, metrics.RowsSkipped);
        Assert.Equal(2, metrics.Warnings.Count(w => w.StartsWith("Fila", StringComparison.Ordinal)));
        Assert.Equal(1, metrics.RowsTotal); // RowsTotal sigue contando solo las aceptadas
    }
}
