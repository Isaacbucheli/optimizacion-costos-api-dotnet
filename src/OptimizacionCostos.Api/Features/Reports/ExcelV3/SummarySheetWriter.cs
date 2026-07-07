using ClosedXML.Excel;

namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>
/// Hoja "Resumen Ejecutivo" (primera hoja del workbook, activa al abrir). Agrega los resultados de
/// costo por servicio usando <see cref="ComparableTotalsCalculator"/> (NO reimplementa la
/// matemática de totales/ahorros), agrega el bloque de escenarios y un bloque de metodología con
/// viñetas generadas a partir de los conteos reales (nada hardcodeado ni oculto).
/// </summary>
public static class SummarySheetWriter
{
    private const string SheetName = "Resumen Ejecutivo";

    public static void Write(XLWorkbook wb, string clientName, string analysisName, DateTime? createdAt,
        DateTime generatedAt, IReadOnlyList<(string ServiceLabel, IReadOnlyList<Dictionary<string, object?>> Rows)> services,
        IReadOnlyList<ScenarioV3> scenarios, int variableCount, int manualCount)
    {
        var ws = wb.Worksheets.Add(SheetName);
        ws.Position = 1; // primera hoja del workbook

        var row = 1;

        // --- Título + subtítulo ---
        var titleCell = ws.Cell(row, 1);
        titleCell.SetValue($"{clientName} — Optimización de costos Azure");
        titleCell.Style.Font.Bold = true;
        titleCell.Style.Font.FontSize = 16;
        titleCell.Style.Font.FontColor = XLColor.FromHtml("#2E7D32");
        row++;

        var createdLabel = createdAt is { } c ? c.ToString("yyyy-MM-dd") : "-";
        var subtitleCell = ws.Cell(row, 1);
        subtitleCell.SetValue($"{analysisName} · Análisis: {createdLabel} · Generado: {generatedAt:yyyy-MM-dd} · Cifras mensuales en USD.");
        subtitleCell.Style.Font.FontColor = XLColor.FromHtml("#666666");
        row += 2; // fila en blanco de separación

        // --- Tabla por servicio (solo con datos) ---
        row = WriteServicesTable(ws, row, services, out var grandTotals);

        row += 1;

        // --- Bloque Escenarios ---
        if (scenarios.Count > 0)
            row = WriteScenariosBlock(ws, row, scenarios);

        row += 1;

        // --- Bloque Metodología ---
        WriteMethodologyBlock(ws, row, variableCount, manualCount);

        ws.Columns().AdjustToContents();
    }

    /// <summary>
    /// Tabla por servicio: una fila por cada servicio con datos + fila TOTAL. Usa
    /// ComparableTotalsCalculator (vía SheetWriter.ToTotalsInput) para no duplicar la matemática de
    /// totales/ahorros del bloque de totales de las hojas de detalle.
    /// </summary>
    private static int WriteServicesTable(IXLWorksheet ws, int startRow,
        IReadOnlyList<(string ServiceLabel, IReadOnlyList<Dictionary<string, object?>> Rows)> services,
        out ComparableTotals grandTotals)
    {
        string[] headers =
        [
            "Servicio", "Recursos", "PAYG mensual", "Total optimizado 1A", "Total optimizado 3A",
            "Ahorro 1A $", "Ahorro 1A %", "Ahorro 3A $", "Ahorro 3A %",
        ];
        var headerRow = startRow;
        for (var col = 0; col < headers.Length; col++)
            ws.Cell(headerRow, col + 1).SetValue(headers[col]);
        ExcelStyles.HeaderRow(ws, headerRow, headers.Length);

        var row = headerRow + 1;
        var allRows = new List<Dictionary<string, object?>>();

        foreach (var (label, rows) in services)
        {
            if (rows.Count == 0) continue; // solo servicios con datos

            var totals = ComparableTotalsCalculator.Compute(rows.Select(SheetWriter.ToTotalsInput).ToList());
            allRows.AddRange(rows);

            WriteServiceRow(ws, row, label, rows.Count, totals);
            row++;
        }

        grandTotals = ComparableTotalsCalculator.Compute(allRows.Select(SheetWriter.ToTotalsInput).ToList());
        if (allRows.Count > 0)
        {
            // Fila TOTAL como fórmulas SUM intra-hoja sobre las filas de servicio: al editar un
            // servicio, el total recalcula. La tabla del Resumen NO tiene autofiltro, así que SUM
            // basta (no hace falta SUBTOTAL como en las hojas de detalle filtrables).
            WriteTotalRowFormulas(ws, row, firstRow: headerRow + 1, lastRow: row - 1, resourceCount: allRows.Count);
            ExcelStyles.SubtotalRow(ws.Row(row), grand: true);
            row++;
        }

        return row;
    }

    private static void WriteTotalRowFormulas(IXLWorksheet ws, int row, int firstRow, int lastRow, int resourceCount)
    {
        ws.Cell(row, 1).SetValue("TOTAL");
        ws.Cell(row, 2).SetValue(resourceCount);
        SetMoneyFormula(ws.Cell(row, 3), $"=SUM(C{firstRow}:C{lastRow})");   // PAYG mensual
        SetMoneyFormula(ws.Cell(row, 4), $"=SUM(D{firstRow}:D{lastRow})");   // Total optimizado 1A
        SetMoneyFormula(ws.Cell(row, 5), $"=SUM(E{firstRow}:E{lastRow})");   // Total optimizado 3A
        SetMoneyFormula(ws.Cell(row, 6), $"=SUM(F{firstRow}:F{lastRow})");   // Ahorro 1A $
        SetPercentFormula(ws.Cell(row, 7), $"=IF(C{row}>0,F{row}/C{row},0)"); // Ahorro 1A %
        SetMoneyFormula(ws.Cell(row, 8), $"=SUM(H{firstRow}:H{lastRow})");   // Ahorro 3A $
        SetPercentFormula(ws.Cell(row, 9), $"=IF(C{row}>0,H{row}/C{row},0)"); // Ahorro 3A %
    }

    private static void WriteServiceRow(IXLWorksheet ws, int row, string label, int resourceCount, ComparableTotals totals)
    {
        ws.Cell(row, 1).SetValue(label);
        ws.Cell(row, 2).SetValue(resourceCount);
        SetMoney(ws.Cell(row, 3), totals.PaygTotal);
        SetMoney(ws.Cell(row, 4), totals.Total1);
        SetMoney(ws.Cell(row, 5), totals.Total3);
        SetMoney(ws.Cell(row, 6), totals.Savings1);
        SetPercent(ws.Cell(row, 7), totals.Savings1Pct);
        SetMoney(ws.Cell(row, 8), totals.Savings3);
        SetPercent(ws.Cell(row, 9), totals.Savings3Pct);
    }

    /// <summary>Bloque Escenarios: nombre | total mensual | ahorro mensual | % ahorro; resalta el de mayor ahorro.</summary>
    private static int WriteScenariosBlock(IXLWorksheet ws, int startRow, IReadOnlyList<ScenarioV3> scenarios)
    {
        var titleCell = ws.Cell(startRow, 1);
        titleCell.SetValue("ESCENARIOS");
        titleCell.Style.Font.Bold = true;
        titleCell.Style.Font.FontColor = XLColor.FromHtml("#424242");
        titleCell.Style.Font.FontSize = 12;

        var headerRow = startRow + 1;
        string[] headers = ["Escenario", "Total mensual", "Ahorro mensual", "% ahorro"];
        for (var col = 0; col < headers.Length; col++)
            ws.Cell(headerRow, col + 1).SetValue(headers[col]);
        ExcelStyles.HeaderRow(ws, headerRow, headers.Length);

        var bestNumber = scenarios.OrderByDescending(s => s.SavingsMonthly).First().Number;

        var row = headerRow + 1;
        foreach (var sc in scenarios)
        {
            ws.Cell(row, 1).SetValue($"Escenario #{sc.Number} — {sc.Name}");
            SetMoney(ws.Cell(row, 2), sc.TotalMonthly);
            SetMoney(ws.Cell(row, 3), sc.SavingsMonthly);
            SetPercent(ws.Cell(row, 4), sc.SavingsPct);

            if (sc.Number == bestNumber)
            {
                var highlightRow = ws.Row(row);
                highlightRow.Style.Fill.PatternType = XLFillPatternValues.Solid;
                highlightRow.Style.Fill.BackgroundColor = XLColor.FromHtml(ExcelStyles.HeaderHex);
            }
            row++;
        }

        return row;
    }

    /// <summary>
    /// Bloque de metodología: viñetas generadas a partir de conteos reales (nada hardcodeado). Cubre
    /// fuente de precios, supuesto de horas/mes, recursos con precio variable/costo manual (no suman
    /// al total optimizable) y exclusión de recursos ya reservados.
    /// </summary>
    private static void WriteMethodologyBlock(IXLWorksheet ws, int startRow, int variableCount, int manualCount)
    {
        var titleCell = ws.Cell(startRow, 1);
        titleCell.SetValue("METODOLOGÍA");
        titleCell.Style.Font.Bold = true;
        titleCell.Style.Font.FontColor = XLColor.FromHtml("#424242");

        var bullets = new List<string>
        {
            "• Precios provenientes de Azure Retail Prices API (públicos, sin descuentos EA/CSP).",
            "• Cálculo mensual asumiendo 730 horas/mes.",
            $"• {variableCount} recurso(s) con precio variable (consumo real, no por tiempo) — no suman al total optimizable.",
            $"• {manualCount} recurso(s) con costo manual (sin precio en Retail API) — no suman al total optimizable.",
            "• Los recursos ya reservados (Reserved Instances confirmadas) se excluyen de la recomendación de optimización.",
            "• Moneda: USD.",
        };

        var row = startRow + 1;
        foreach (var bullet in bullets)
        {
            var cell = ws.Cell(row, 1);
            cell.SetValue(bullet);
            cell.Style.Font.FontColor = XLColor.FromHtml("#444444");
            cell.Style.Font.FontSize = 9;
            row++;
        }
    }

    private static void SetMoney(IXLCell cell, double value)
    {
        cell.SetValue(value);
        cell.Style.NumberFormat.Format = ExcelStyles.Money;
    }

    private static void SetPercent(IXLCell cell, double value)
    {
        cell.SetValue(value);
        cell.Style.NumberFormat.Format = ExcelStyles.Percent;
    }

    private static void SetMoneyFormula(IXLCell cell, string formulaA1)
    {
        cell.FormulaA1 = formulaA1;
        cell.Style.NumberFormat.Format = ExcelStyles.Money;
    }

    private static void SetPercentFormula(IXLCell cell, string formulaA1)
    {
        cell.FormulaA1 = formulaA1;
        cell.Style.NumberFormat.Format = ExcelStyles.Percent;
    }
}
