using System.Linq;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using OptimizacionCostos.Api.Features.Waf;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Reproducción del bug "el Excel WAF exportado no abre" (regresión tras la migración a .NET).
/// Ejercita el port completo (cargar plantilla + ClosedXML + reconstrucción del ZIP) sin BD ni
/// auth y valida que el paquete resultante sea un .xlsx abrible con la hoja Resultados.
/// </summary>
public sealed class WafExcelExporterTests
{
    private static WafExportRow Row(int pillar, string scope, int impact) => new(
        CanonicalId: 1, PillarNumber: pillar, ReviewScopeEs: scope, BenefitEs: "Beneficio",
        ClientActionEs: "Acción cliente", BitActionEs: "Acción BIT",
        Resources: new[] { "vm-1", "vm-2" }, ResourceCount: 2, CompletionPct: 40,
        LastSeenAt: new DateTime(2026, 5, 1), BusinessImpact: "High", ImpactNumber: impact,
        PriorityOverride: null, RemediationStartDate: new DateTime(2026, 4, 1),
        ProjectedBitEffort: "2 días", ExecutionLog: "Log de ejecución");

    [Fact]
    public async Task ExportAsync_ProduceUnLibroAbrible()
    {
        var exporter = new ClosedXmlWafExporter();
        var rows = new[] { Row(2, "MFA admins", 1), Row(5, "Reserved Instances", 2) };

        var bytes = await exporter.ExportAsync(clientId: 1, rows);

        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms); // lanza si el paquete OOXML es inválido → reproduce el bug
        Assert.True(wb.Worksheets.TryGetWorksheet("Resultados", out _));
    }

    [Fact]
    public async Task ExportAsync_PasaValidacionOpenXml()
    {
        var exporter = new ClosedXmlWafExporter();
        var rows = new[] { Row(2, "MFA admins", 1), Row(5, "Reserved Instances", 2) };
        var bytes = await exporter.ExportAsync(clientId: 1, rows);

        var tmp = Path.Combine(Path.GetTempPath(), "waf-export-validate.xlsx");
        await File.WriteAllBytesAsync(tmp, bytes);
        try
        {
            using var doc = SpreadsheetDocument.Open(tmp, false);
            var validator = new OpenXmlValidator();
            // El atributo legacy 'shapeId' en los comentarios de la plantilla lo escribe Excel y lo
            // tolera al abrir (existía igual en la exportación Python que abría bien). El validador es
            // más estricto que Excel ahí, así que se ignora; lo demás debe estar limpio.
            var errors = validator.Validate(doc)
                .Where(e => !e.Description.Contains("shapeId"))
                .ToList();
            var top = string.Join("\n", errors.Take(20).Select(e =>
                $"[{e.Id}] {e.Description} | Part={e.Part?.Uri} | Path={e.Path?.XPath}"));
            Assert.True(errors.Count == 0, $"OpenXmlValidator encontró {errors.Count} problemas:\n{top}");
        }
        finally { File.Delete(tmp); }
    }
}
