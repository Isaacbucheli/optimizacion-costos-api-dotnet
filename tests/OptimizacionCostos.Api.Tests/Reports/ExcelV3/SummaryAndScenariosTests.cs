using ClosedXML.Excel;
using OptimizacionCostos.Api.Features.Reports.ExcelV3;
namespace OptimizacionCostos.Api.Tests.Reports.ExcelV3;

public class SummaryAndScenariosTests
{
    private static Dictionary<string, object?> Row(double payg, double? ri1) =>
        new(StringComparer.OrdinalIgnoreCase) { ["payg_monthly"] = payg, ["ri_1y_monthly"] = ri1, ["ri_3y_monthly"] = ri1, ["ri_coverage"] = null };

    private static ScenarioV3 Esc(int n, string name, double total, double sav) =>
        new(n, name, null, total, total * 12, sav, sav * 12, total + sav > 0 ? sav / (total + sav) : 0,
            new List<ScenarioLineV3> { new("VMs", total, null) });

    [Fact]
    public void Resumen_agrega_por_servicio_con_totales_comparables()
    {
        using var wb = new XLWorkbook();
        var services = new List<(string, IReadOnlyList<Dictionary<string, object?>>)>
        {
            ("Virtual Machines", new List<Dictionary<string, object?>> { Row(100, 60), Row(50, null) }),
        };
        SummarySheetWriter.Write(wb, "CLIENTE X", "Analisis Q3", new DateTime(2026, 7, 1), new DateTime(2026, 7, 7),
            services, new List<ScenarioV3> { Esc(1, "Esc 1", 380, 50) }, variableCount: 2, manualCount: 1);

        var ws = wb.Worksheet("Resumen Ejecutivo");
        Assert.Contains("CLIENTE X", ws.Cell(1, 1).GetString());
        var flat = ws.CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains("Virtual Machines", flat);
        Assert.Contains(flat, s => s.Contains("precio variable")); // metodología dinámica
        // Total optimizado 1A del servicio: 60 + 50 = 110 (en alguna celda numérica de la fila del servicio)
        var rowVm = ws.RowsUsed().First(r => r.Cell(1).GetString() == "Virtual Machines");
        Assert.Contains(rowVm.CellsUsed(), c => c.DataType == XLDataType.Number && Math.Abs(c.GetDouble() - 110) < 0.01);
    }

    [Fact]
    public void Escenarios_layout_comparativo_con_valores_escritos()
    {
        using var wb = new XLWorkbook();
        ScenariosSheetWriter.Write(wb, 430,
            new List<ScenarioLineV3> { new("VMs Optimización", 430, null) },
            new List<ScenarioV3> { Esc(1, "RI 1 año", 350, 80), Esc(2, "RI 3 años", 335, 95) });
        var ws = wb.Worksheet("Escenarios");
        var flat = ws.CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains(flat, s => s.Contains("Escenario #1"));
        Assert.Contains(flat, s => s.Contains("Escenario #2"));
        // ningún texto con el typo histórico y ninguna fórmula
        Assert.DoesNotContain(flat, s => s.Contains("Depuraciión"));
        Assert.All(ws.CellsUsed(), c => Assert.False(c.HasFormula));
        // el mejor escenario (mayor ahorro) resaltado
        Assert.Contains(ws.CellsUsed(), c => c.GetString().Contains("Escenario #2") &&
            c.Style.Fill.BackgroundColor.ColorType == XLColorType.Color);
    }

    [Fact]
    public void Resumen_no_crea_filas_de_servicios_sin_datos()
    {
        using var wb = new XLWorkbook();
        SummarySheetWriter.Write(wb, "C", "A", null, DateTime.Today,
            new List<(string, IReadOnlyList<Dictionary<string, object?>>)>(), new List<ScenarioV3>(), 0, 0);
        var ws = wb.Worksheet("Resumen Ejecutivo");
        Assert.DoesNotContain(ws.CellsUsed().Select(c => c.GetString()), s => s == "Virtual Machines");
    }
}
