using System.Text.Json;
using ClosedXML.Excel;
using OptimizacionCostos.Api.Features.Optimization;

namespace OptimizacionCostos.Api.Tests.Optimization;

public class OptimizationExcelExporterTests
{
    private static IReadOnlyDictionary<string, object?> F(
        string checkId, string name, double? savings, string state = "abierto",
        string severity = "medium", string detailsJson = "{}") => new Dictionary<string, object?>
    {
        ["check_id"] = checkId, ["category"] = "cost_waste", ["severity"] = severity,
        ["subscription_id"] = "sub-1",
        ["azure_resource_id"] = $"/subscriptions/sub-1/resourceGroups/rg-app/providers/x/y/{name}",
        ["resource_name"] = name, ["resource_type"] = "t", ["region"] = "eastus2",
        ["details"] = JsonSerializer.Deserialize<JsonElement>(detailsJson),
        ["estimated_monthly_savings"] = savings, ["currency"] = "USD",
        ["fingerprint"] = "ab", ["state"] = state, ["notes"] = null,
    };

    private static readonly OptExcelScanInfo Scan = new("Banco Delta", new DateTime(2026, 7, 3), 12);

    private static XLWorkbook Wb(IReadOnlyList<IReadOnlyDictionary<string, object?>> findings, IReadOnlyList<string>? states = null)
    {
        var r = new OptimizationExcelExporter().Generate(Scan, findings, states ?? []);
        return new XLWorkbook(new MemoryStream(r.Bytes));
    }

    [Fact]
    public void Estructura_resumen_primero_y_solo_grupos_con_datos_sin_hojas_ocultas()
    {
        using var wb = Wb([
            F("vms_without_ahb", "vm-1", 200),
            F("orphaned_disks", "disk-1", 50),
        ]);
        var names = wb.Worksheets.Select(w => w.Name).ToList();
        Assert.Equal(new[] { "Resumen", "Azure Hybrid Benefit", "Storage" }, names); // orden de GroupOrder, sin Compute/Networking
        Assert.All(wb.Worksheets, w => Assert.Equal(XLWorksheetVisibility.Visible, w.Visibility));
    }

    [Fact]
    public void Hoja_de_grupo_ordena_por_ahorro_desc_y_tiene_fila_total()
    {
        using var wb = Wb([
            F("orphaned_disks", "disk-chico", 10),
            F("old_snapshots", "snap-grande", 90, severity: "low"),
        ]);
        var ws = wb.Worksheet("Storage");
        Assert.Equal("Recomendación", ws.Cell(1, 1).GetString()); // encabezado
        Assert.Equal("snap-grande", ws.Cell(2, 2).GetString());   // mayor ahorro primero
        Assert.Equal("disk-chico", ws.Cell(3, 2).GetString());
        Assert.Equal("TOTAL (2)", ws.Cell(4, 1).GetString());
        Assert.Equal(100d, ws.Cell(4, 7).GetDouble(), 2);
        Assert.Equal("rg-app", ws.Cell(2, 3).GetString());        // RG parseado
        Assert.Equal("Baja", ws.Cell(2, 8).GetString());          // severidad traducida
        Assert.Equal("Abierto", ws.Cell(2, 9).GetString());       // estado traducido
    }

    [Fact]
    public void Resumen_muestra_kpis_estados_incluidos_y_tabla_por_categoria()
    {
        using var wb = Wb([
            F("vms_without_ahb", "vm-1", 200),
            F("orphaned_disks", "disk-1", 50, state: "en_progreso"),
        ], ["abierto", "en_progreso"]);
        var texts = wb.Worksheet("Resumen").CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains("Banco Delta — Oportunidades de Optimización Azure", texts);
        Assert.Contains(texts, t => t.Contains("Estados incluidos: Abierto, En progreso"));
        Assert.Contains(texts, t => t.Contains("12 suscripciones"));
        Assert.Contains("Azure Hybrid Benefit", texts); // fila de la tabla por categoría
        Assert.Contains("Azure Hybrid Benefit no aplicado", texts); // fila de la tabla por check
        Assert.Contains(texts, t => t.Contains("Retail Prices")); // nota metodológica
    }

    [Fact]
    public void Sin_hallazgos_genera_solo_resumen_valido()
    {
        using var wb = Wb([]);
        Assert.Single(wb.Worksheets);
        Assert.Equal("Resumen", wb.Worksheet(1).Name);
        var texts = wb.Worksheet("Resumen").CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains(texts, t => t.Contains("Estados incluidos: Todos"));
    }

    [Fact]
    public void Nombre_de_archivo_con_slug_del_cliente_y_fecha_del_barrido()
    {
        var r = new OptimizationExcelExporter().Generate(Scan, [], []);
        Assert.Equal("oportunidades-optimizacion-Banco-Delta-20260703.xlsx", r.FileName);
    }
}
