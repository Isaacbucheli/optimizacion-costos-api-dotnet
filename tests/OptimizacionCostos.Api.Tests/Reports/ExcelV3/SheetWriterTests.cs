using ClosedXML.Excel;
using OptimizacionCostos.Api.Features.Reports.ExcelV3;

namespace OptimizacionCostos.Api.Tests.Reports.ExcelV3;

public class SheetWriterTests
{
    private static Dictionary<string, object?> Row(string name, double payg, double? ri1, double? ri3, string? coverage = null) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["resource_name"] = name, ["payg_monthly"] = payg,
            ["ri_1y_monthly"] = ri1, ["ri_3y_monthly"] = ri3, ["ri_coverage"] = coverage,
        };

    private static SheetSpec Spec() => new("Prueba", new List<ColumnSpec>
    {
        new("Recurso", ColKind.Text, r => r["resource_name"]),
        new("PAYG mes", ColKind.Money, r => r["payg_monthly"], Role: MoneyRole.Payg),
        new("RI 1A mes", ColKind.Money, r => r["ri_1y_monthly"], Role: MoneyRole.Ri1),
        new("RI 3A mes", ColKind.Money, r => r["ri_3y_monthly"], Role: MoneyRole.Ri3),
    });

    [Fact]
    public void Escribe_datos_y_bloque_de_totales_comparables()
    {
        using var wb = new XLWorkbook();
        var rows = new List<Dictionary<string, object?>> { Row("a", 100, 60, 55), Row("b", 50, null, null) };
        var ws = SheetWriter.Write(wb, Spec(), rows);

        Assert.Equal("Recurso", ws.Cell(1, 1).GetString());
        Assert.Equal("a", ws.Cell(2, 1).GetString());
        // filas: 1 header + 2 datos + 3 totales
        Assert.Equal("Subtotal elegible a RI (1)", ws.Cell(4, 1).GetString());
        Assert.Equal("Subtotal no elegible (1)", ws.Cell(5, 1).GetString());
        Assert.Equal("TOTAL (2)", ws.Cell(6, 1).GetString());
        Assert.Equal(100, ws.Cell(4, 2).GetDouble(), 2);   // payg elegible
        Assert.Equal(50, ws.Cell(5, 3).GetDouble(), 2);    // no elegible paga PAYG en columna RI
        Assert.Equal(110, ws.Cell(6, 3).GetDouble(), 2);   // Total optimizado 1A = 60 + 50
        Assert.Equal(150, ws.Cell(6, 2).GetDouble(), 2);   // PAYG total
    }

    [Fact]
    public void Autofiltro_cubre_todas_las_columnas_y_freeze_fila_1()
    {
        using var wb = new XLWorkbook();
        var ws = SheetWriter.Write(wb, Spec(), new List<Dictionary<string, object?>> { Row("a", 1, null, null) });
        Assert.NotNull(ws.AutoFilter);
        Assert.Equal(4, ws.AutoFilter.Range.ColumnCount());
        Assert.Equal(1, ws.SheetView.SplitRow);
    }

    [Fact]
    public void Reservado_confirmado_va_al_subtotal_no_elegible()
    {
        using var wb = new XLWorkbook();
        var rows = new List<Dictionary<string, object?>> { Row("r", 200, 200, 200, "confirmed"), Row("e", 100, 60, 50) };
        var ws = SheetWriter.Write(wb, Spec(), rows);
        Assert.Equal("Subtotal elegible a RI (1)", ws.Cell(4, 1).GetString());
        Assert.Equal(260, ws.Cell(6, 3).GetDouble(), 2);   // 60 + 200
    }

    [Fact]
    public void Formato_de_moneda_unico()
    {
        using var wb = new XLWorkbook();
        var ws = SheetWriter.Write(wb, Spec(), new List<Dictionary<string, object?>> { Row("a", 1.5, null, null) });
        Assert.Equal(ExcelStyles.Money, ws.Cell(2, 2).Style.NumberFormat.Format);
    }
}
