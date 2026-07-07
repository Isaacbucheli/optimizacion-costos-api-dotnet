using ClosedXML.Excel;
using OptimizacionCostos.Api.Features.Reports.ExcelV3;
namespace OptimizacionCostos.Api.Tests.Reports.ExcelV3;

/// <summary>Fake en memoria de ICostExcelDataSourceV3: 2 filas de VMs (una elegible a RI, otra no) + 1 escenario.</summary>
public sealed class FakeDataSourceV3 : ICostExcelDataSourceV3
{
    public ExcelV3Data Data { get; set; } = Default();

    public static ExcelV3Data Default()
    {
        Dictionary<string, object?> Vm(string n, double payg, double? ri1) => new(StringComparer.OrdinalIgnoreCase)
        {
            ["resource_name"] = n, ["resource_group"] = "rg", ["location"] = "eastus2", ["payg_monthly"] = payg,
            ["ri_1y_monthly"] = ri1, ["ri_3y_monthly"] = ri1, ["ri_coverage"] = null, ["calculation_status"] = "calculated",
        };
        var vms = new List<Dictionary<string, object?>> { Vm("vm-a", 100, 60), Vm("vm-b", 50, null) };
        return new ExcelV3Data("Cliente Prueba", "Analisis 1", new DateTime(2026, 7, 1),
            new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase)
            { ["vms"] = vms, ["results"] = vms },
            new List<ScenarioV3> { new(1, "RI 1 año", null, 110, 1320, 40, 480, 40.0 / 150, new List<ScenarioLineV3> { new("VMs", 110, null) }) },
            new List<ScenarioLineV3> { new("VMs", 150, null) }, 150);
    }

    public Task<ExcelV3Data> LoadAsync(int analysisId, CancellationToken ct) => Task.FromResult(Data);
}

public class CodeExporterTests
{
    private static XLWorkbook Generate(decimal? margin = null)
    {
        var exp = new CodeCostExcelExporter(new FakeDataSourceV3());
        var result = exp.GenerateAsync(1, margin, CancellationToken.None).GetAwaiter().GetResult();
        return new XLWorkbook(new MemoryStream(result.Bytes));
    }

    [Fact]
    public void Estructura_sin_hojas_vacias_ni_ocultas_y_resumen_activo()
    {
        using var wb = Generate();
        var names = wb.Worksheets.Select(w => w.Name).ToList();
        Assert.Equal("Resumen Ejecutivo", names[0]);                 // Resumen primero
        Assert.Equal("Detalle de recursos", names[^1]);              // Detalle (anexo) último
        Assert.Equal("Escenarios", names[^2]);                       // Escenarios penúltima (última que se revisa)
        Assert.Contains("Optimización VMs", names);
        Assert.DoesNotContain("Redis", names);                       // sin datos → sin hoja
        Assert.DoesNotContain("Servicios no catalogados", names);    // fuera del entregable
        Assert.All(wb.Worksheets, w => Assert.Equal(XLWorksheetVisibility.Visible, w.Visibility));
    }

    [Fact]
    public void Nombre_de_archivo_con_cliente_y_fecha()
    {
        var exp = new CodeCostExcelExporter(new FakeDataSourceV3());
        var r = exp.GenerateAsync(1, null, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Matches(@"^Optimizacion-Costos-Cliente-Prueba-\d{8}\.xlsx$", r.FileName);
    }

    [Fact]
    public void Margen_escala_montos_sin_mencionarse_y_los_pct_no_cambian()
    {
        using var sin = Generate(null);
        using var con = Generate(10m);
        double Cell(XLWorkbook wb, string sheet, int r, int c) => wb.Worksheet(sheet).Cell(r, c).GetDouble();
        // fila de datos de la VM a: PAYG columna 4 (Recurso, Grupo, Región, ...) — localizar por header
        var wsSin = sin.Worksheet("Optimización VMs"); var wsCon = con.Worksheet("Optimización VMs");
        int paygCol = wsSin.Row(1).CellsUsed().First(c => c.GetString() == "PAYG mes").Address.ColumnNumber;
        Assert.Equal(Cell(sin, "Optimización VMs", 2, paygCol) * 1.10, Cell(con, "Optimización VMs", 2, paygCol), 2);
        // ninguna celda del libro menciona margen
        Assert.DoesNotContain(con.Worksheets.SelectMany(w => w.CellsUsed()).Select(c => c.GetString()),
            s => s.ToLowerInvariant().Contains("margen"));
    }

    [Fact]
    public void Resumen_pliega_sql_vm_bajo_vms_sin_fila_separada()
    {
        // Datos: 1 VM regular + 1 sql_vm (metadatos de SQL Server en VM)
        Dictionary<string, object?> Row(string key, string name, double payg, string? displayName = null)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["service_key"] = key,
                ["resource_name"] = name,
                ["payg_monthly"] = payg,
                ["ri_1y_monthly"] = payg * 0.6,
                ["ri_3y_monthly"] = payg * 0.6,
                ["ri_coverage"] = null,
                ["calculation_status"] = "calculated",
            };
            if (displayName is not null)
                row["display_name"] = displayName;
            return row;
        }

        var dataWithSqlVm = new ExcelV3Data(
            "TestClient", "TestAnalysis", new DateTime(2026, 7, 1),
            new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase)
            {
                ["results"] = new List<Dictionary<string, object?>>
                {
                    Row("vms", "vm-regular", 100, "Virtual Machines"),
                    Row("sql_vm", "vm-with-sql", 50),  // sin display_name: debe usar el de "vms"
                },
                ["vms"] = new List<Dictionary<string, object?>>
                {
                    Row("vms", "vm-regular", 100, "Virtual Machines"),
                },
            },
            new List<ScenarioV3>(),
            new List<ScenarioLineV3> { new("VMs", 150, null) }, 150);

        var source = new FakeDataSourceV3 { Data = dataWithSqlVm };
        var exp = new CodeCostExcelExporter(source);
        var result = exp.GenerateAsync(1, null, CancellationToken.None).GetAwaiter().GetResult();
        using var wb = new XLWorkbook(new MemoryStream(result.Bytes));

        var resumen = wb.Worksheet("Resumen Ejecutivo");
        var allText = resumen.CellsUsed().Select(c => c.GetString()).ToList();

        // Debe haber "Virtual Machines" (display_name del visible key)
        Assert.Contains(allText, s => s.Contains("Virtual Machines"));

        // NO debe haber "sql_vm" ni "SQL-en-VM" como fila separada en el Resumen
        Assert.DoesNotContain(allText, s => s.Equals("sql_vm", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allText, s => s.Equals("SQL-en-VM", StringComparison.OrdinalIgnoreCase));

        // La fila de VMs debe sumar ambos costos: 100 + 50 = 150
        var vmRow = resumen.RowsUsed().FirstOrDefault(r =>
            r.CellsUsed().Any(c => c.GetString().Contains("Virtual Machines")));
        Assert.NotNull(vmRow);
        var numCells = vmRow.CellsUsed().Where(c => c.DataType == XLDataType.Number).ToList();
        Assert.Contains(numCells, c => Math.Abs(c.GetDouble() - 150) < 0.01);  // total = 100+50
    }
}
