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

    /// <summary>Spec con la forma real que SheetCatalog.BaseColumns da a toda hoja de servicio:
    /// Recurso(A), PAYG(B), RI1(C), RI3(D), Elegible(E), Ahorro1$(F), Ahorro3$(G), Ahorro1%(H), Ahorro3%(I).
    /// Con esta forma, el bloque TOTAL escribe SUBTOTAL(9) + optimizado = PAYG−Ahorro.</summary>
    private static SheetSpec Spec() => new("Prueba", new List<ColumnSpec>
    {
        new("Recurso", ColKind.Text, r => r["resource_name"]),
        new("PAYG mes", ColKind.Money, r => r["payg_monthly"], Role: MoneyRole.Payg),
        new("RI 1A mes", ColKind.Money, r => r["ri_1y_monthly"], Role: MoneyRole.Ri1),
        new("RI 3A mes", ColKind.Money, r => r["ri_3y_monthly"], Role: MoneyRole.Ri3),
        new("Elegible a RI", ColKind.Eligibility, r =>
            ComparableTotalsCalculator.IsEligible(
                r["ri_1y_monthly"] as double?, r["ri_3y_monthly"] as double?,
                string.Equals(r.TryGetValue("ri_coverage", out var cov) ? cov as string : null, "confirmed", StringComparison.OrdinalIgnoreCase))
                ? "Sí" : "No"),
        new("Ahorro 1A $", ColKind.Money, r => null, Role: MoneyRole.Savings1Usd),
        new("Ahorro 3A $", ColKind.Money, r => null, Role: MoneyRole.Savings3Usd),
        new("Ahorro 1A %", ColKind.Percent, r => null, Role: MoneyRole.Savings1Pct),
        new("Ahorro 3A %", ColKind.Percent, r => null, Role: MoneyRole.Savings3Pct),
    });

    // Columnas (1-based) para esta forma de hoja.
    private const int ColPayg = 2, ColRi1 = 3, ColRi3 = 4, ColElig = 5, ColSav1 = 6, ColSav3 = 7, ColSav1Pct = 8, ColSav3Pct = 9;

    [Fact]
    public void Escribe_datos_encabezados_y_una_sola_fila_TOTAL()
    {
        using var wb = new XLWorkbook();
        var rows = new List<Dictionary<string, object?>> { Row("a", 100, 60, 55), Row("b", 50, null, null) };
        var ws = SheetWriter.Write(wb, Spec(), rows);

        Assert.Equal("Recurso", ws.Cell(1, 1).GetString());
        Assert.Equal("a", ws.Cell(2, 1).GetString());
        Assert.Equal("Sí", ws.Cell(2, ColElig).GetString());   // 'a' elegible (tiene RI)
        Assert.Equal("No", ws.Cell(3, ColElig).GetString());   // 'b' no elegible (sin RI)

        // 1 header + 2 datos + 1 TOTAL (fila 4). NO hay filas de desglose por elegibilidad.
        Assert.Equal("TOTAL (2)", ws.Cell(4, 1).GetString());
        Assert.True(ws.Cell(5, 1).IsEmpty(), "no debe haber una 4ª fila (desglose eliminado)");
        var col1Texts = ws.Column(1).CellsUsed().Select(c => c.GetString());
        Assert.DoesNotContain(col1Texts, t => t.Contains("Subtotal elegible") || t.Contains("Subtotal no elegible"));
    }

    [Fact]
    public void TOTAL_usa_SUBTOTAL_9_y_optimizado_es_PAYG_menos_Ahorro()
    {
        using var wb = new XLWorkbook();
        var rows = new List<Dictionary<string, object?>> { Row("a", 100, 60, 55), Row("b", 50, null, null) };
        var ws = SheetWriter.Write(wb, Spec(), rows);
        const int TR = 4; // fila TOTAL

        // PAYG y Ahorros $ del TOTAL = SUBTOTAL(9,...) sobre el rango de datos (filas 2..3) → sensible al filtro.
        Assert.True(ws.Cell(TR, ColPayg).HasFormula);
        Assert.Contains("SUBTOTAL(9,B2:B3", ws.Cell(TR, ColPayg).FormulaA1);
        Assert.True(ws.Cell(TR, ColSav1).HasFormula);
        Assert.Contains("SUBTOTAL(9,F2:F3", ws.Cell(TR, ColSav1).FormulaA1);
        Assert.Contains("SUBTOTAL(9,G2:G3", ws.Cell(TR, ColSav3).FormulaA1);

        // RI 1A/3A del TOTAL = "Total optimizado" = PAYG − Ahorro (referencia la propia fila TOTAL).
        Assert.True(ws.Cell(TR, ColRi1).HasFormula);
        Assert.Contains("B4-F4", ws.Cell(TR, ColRi1).FormulaA1);   // optimizado 1A = PAYG − Ahorro1
        Assert.Contains("B4-G4", ws.Cell(TR, ColRi3).FormulaA1);   // optimizado 3A = PAYG − Ahorro3

        // Ahorro % del TOTAL = ahorro/PAYG sobre los totales ya sensibles al filtro.
        Assert.Contains("IF(B4>0,F4/B4,0)", ws.Cell(TR, ColSav1Pct).FormulaA1);
        Assert.Contains("IF(B4>0,G4/B4,0)", ws.Cell(TR, ColSav3Pct).FormulaA1);
    }

    [Fact]
    public void Ahorro_por_fila_es_formula_viva_y_evalua_correcto()
    {
        using var wb = new XLWorkbook();
        // a: elegible (100/60/55) → ahorro1=40, ahorro3=45 ; b: no elegible (50/null/null) → 0
        var rows = new List<Dictionary<string, object?>> { Row("a", 100, 60, 55), Row("b", 50, null, null) };
        var ws = SheetWriter.Write(wb, Spec(), rows);

        // Fórmula viva por fila (IF/AND) — ClosedXML evalúa IF/AND, así que verificamos el número.
        Assert.True(ws.Cell(2, ColSav1).HasFormula);
        Assert.Equal(40, ws.Cell(2, ColSav1).GetDouble(), 2);       // 100-60
        Assert.Equal(45, ws.Cell(2, ColSav3).GetDouble(), 2);       // 100-55
        Assert.Equal(0.4, ws.Cell(2, ColSav1Pct).GetDouble(), 2);   // 40/100
        Assert.Equal(45.0 / 100, ws.Cell(2, ColSav3Pct).GetDouble(), 2);
        // 'b' no elegible: ahorro 0 (la fórmula IF(AND(Elegible="Sí",...)) da 0)
        Assert.Equal(0, ws.Cell(3, ColSav1).GetDouble(), 2);
        Assert.Equal(0, ws.Cell(3, ColSav3).GetDouble(), 2);
    }

    [Fact]
    public void Reservado_confirmado_no_aporta_ahorro()
    {
        using var wb = new XLWorkbook();
        // r: reservado confirmado (no elegible → ahorro 0, aporta PAYG completo al optimizado)
        // e: elegible (100/60/50)
        var rows = new List<Dictionary<string, object?>> { Row("r", 200, 200, 200, "confirmed"), Row("e", 100, 60, 50) };
        var ws = SheetWriter.Write(wb, Spec(), rows);

        Assert.Equal("No", ws.Cell(2, ColElig).GetString());   // reservado confirmado → No elegible
        Assert.Equal("Sí", ws.Cell(3, ColElig).GetString());   // e elegible
        Assert.Equal(0, ws.Cell(2, ColSav1).GetDouble(), 2);   // reservado: ahorro 0 (no infla)
        Assert.Equal(40, ws.Cell(3, ColSav1).GetDouble(), 2);  // e: 100-60
    }

    [Fact]
    public void Autofiltro_cubre_todas_las_columnas_y_freeze_fila_1()
    {
        using var wb = new XLWorkbook();
        var ws = SheetWriter.Write(wb, Spec(), new List<Dictionary<string, object?>> { Row("a", 1, null, null) });
        Assert.NotNull(ws.AutoFilter);
        Assert.Equal(9, ws.AutoFilter.Range.ColumnCount());   // Spec tiene 9 columnas
        Assert.Equal(1, ws.SheetView.SplitRow);
    }

    [Fact]
    public void Formato_de_moneda_unico()
    {
        using var wb = new XLWorkbook();
        var ws = SheetWriter.Write(wb, Spec(), new List<Dictionary<string, object?>> { Row("a", 1.5, null, null) });
        Assert.Equal(ExcelStyles.Money, ws.Cell(2, ColPayg).Style.NumberFormat.Format);
    }

    [Fact]
    public void Estilos_y_formato_numerico_se_conservan_en_celdas_con_formula()
    {
        using var wb = new XLWorkbook();
        var rows = new List<Dictionary<string, object?>> { Row("a", 100, 60, 55), Row("b", 50, null, null) };
        var ws = SheetWriter.Write(wb, Spec(), rows);

        // Money en ahorros $ (fila de datos y TOTAL)
        Assert.Equal(ExcelStyles.Money, ws.Cell(2, ColSav1).Style.NumberFormat.Format);
        Assert.Equal(ExcelStyles.Money, ws.Cell(4, ColSav1).Style.NumberFormat.Format);
        // Percent en ahorros %
        Assert.Equal(ExcelStyles.Percent, ws.Cell(2, ColSav1Pct).Style.NumberFormat.Format);
        Assert.Equal(ExcelStyles.Percent, ws.Cell(4, ColSav1Pct).Style.NumberFormat.Format);
        // Estilo de fila TOTAL (grand) sigue aplicado (bold)
        Assert.True(ws.Row(4).Style.Font.Bold);
    }
}
