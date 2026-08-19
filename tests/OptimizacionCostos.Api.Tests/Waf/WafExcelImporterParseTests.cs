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
}
