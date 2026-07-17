using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using OptimizacionCostos.Api.Features.Waf;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Reproducción del bug "el Excel WAF exportado no abre" (regresión tras la migración a .NET).
/// Ejercita el port completo (cargar plantilla + ClosedXML) sin BD ni auth y valida que el paquete
/// resultante sea un .xlsx abrible con la hoja Resultados.
/// </summary>
public sealed class WafExcelExporterTests
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static WafExportRow Row(int pillar, string scope, int impact) => new(
        CanonicalId: 1, PillarNumber: pillar, ReviewScopeEs: scope, BenefitEs: "Beneficio " + scope,
        ClientActionEs: "Acción cliente " + scope, BitActionEs: "Acción BIT " + scope,
        Resources: new[] { "vm-1", "vm-2" }, ResourceCount: 2, CompletionPct: 40,
        LastSeenAt: new DateTime(2026, 5, 1), BusinessImpact: "High", ImpactNumber: impact,
        PriorityOverride: null, RemediationStartDate: new DateTime(2026, 4, 1),
        ProjectedBitEffort: "2 días", ExecutionLog: "Log de ejecución " + scope);

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

    /// <summary>
    /// Regresión del bug real "Excel encontró un problema con el contenido": el exportador antiguo
    /// mezclaba el sharedStrings.xml de la plantilla (36 cadenas) con un sheet reindexado por
    /// ClosedXML (índices &gt; 36) → referencias de shared string fuera de rango. El OpenXmlValidator
    /// NO lo detecta (un índice inválido no viola el esquema), así que se verifica a mano que TODA
    /// celda t="s" de TODA hoja apunte dentro de la tabla, con suficientes filas para forzar cadenas
    /// nuevas más allá de las de la plantilla.
    /// </summary>
    [Fact]
    public async Task ExportAsync_TodasLasReferenciasDeSharedStringEnRango()
    {
        var exporter = new ClosedXmlWafExporter();
        // >3 filas por pilar fuerza insert_rows y muchas cadenas nuevas (más allá de la plantilla).
        var rows = Enumerable.Range(0, 8)
            .SelectMany(i => new[]
            {
                Row(1, $"Ambito seguridad {i}", 1),
                Row(5, $"Ambito costos {i}", 2),
            })
            .ToArray();

        var bytes = await exporter.ExportAsync(clientId: 40, rows);

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var sharedCount = CountSharedStrings(zip);

        foreach (var entry in zip.Entries.Where(e =>
                     e.FullName.StartsWith("xl/worksheets/") && e.FullName.EndsWith(".xml")))
        {
            var doc = XDocument.Load(entry.Open());
            var badRefs = doc.Descendants(S + "c")
                .Where(c => (string?)c.Attribute("t") == "s")
                .Select(c => (Cell: (string?)c.Attribute("r"), Index: (int?)c.Element(S + "v")))
                .Where(x => x.Index is null || x.Index >= sharedCount)
                .ToList();

            Assert.True(badRefs.Count == 0,
                $"{entry.FullName}: {badRefs.Count} referencias a shared string fuera de rango " +
                $"(tabla tiene {sharedCount}). Ej: " +
                string.Join(", ", badRefs.Take(5).Select(b => $"{b.Cell}->{b.Index}")));
        }
    }

    /// <summary>
    /// El texto de datos escrito debe poder resolverse de vuelta desde la tabla de shared strings
    /// (no basta con que los índices estén en rango: deben apuntar al texto correcto).
    /// </summary>
    [Fact]
    public async Task ExportAsync_ElTextoDeDatosSeResuelveCorrectamente()
    {
        var exporter = new ClosedXmlWafExporter();
        var rows = new[] { Row(1, "AMBITO_UNICO_DE_PRUEBA", 1) };

        var bytes = await exporter.ExportAsync(clientId: 1, rows);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var sheet = wb.Worksheet("Resultados");
        var found = sheet.CellsUsed().Any(c => c.GetString().Contains("AMBITO_UNICO_DE_PRUEBA"));
        Assert.True(found, "El texto de la fila no se resolvió en la hoja Resultados.");
    }

    private static int CountSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return 0; // sin tabla, no debería haber celdas t="s"
        var doc = XDocument.Load(entry.Open());
        return doc.Root!.Elements(S + "si").Count();
    }
}
