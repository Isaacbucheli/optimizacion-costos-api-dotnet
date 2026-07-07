using ClosedXML.Excel;

namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>Línea de un breakdown (base o de escenario): etiqueta + costo mensual + nota opcional.</summary>
public sealed record ScenarioLineV3(string Label, double MonthlyCost, string? Note);

/// <summary>
/// Escenario de optimización completo, ya calculado aguas arriba (esta hoja NO recalcula nada,
/// solo pinta los valores). SavingsPct viene calculado por el llamador (no se recomputa aquí).
/// </summary>
public sealed record ScenarioV3(int Number, string Name, string? Description,
    double TotalMonthly, double TotalAnnual, double SavingsMonthly, double SavingsAnnual, double SavingsPct,
    IReadOnlyList<ScenarioLineV3> Breakdown);

/// <summary>
/// Hoja "Escenarios": clon del layout comparativo de la hoja "Detalle" de la plantilla vieja
/// (referencia: docs/superpowers/specs/2026-07-07-detalle-dump.txt). Un bloque de 2 columnas
/// (etiqueta + valor) por escenario, separados por una columna angosta, con el bloque base
/// (baseline PAYG) primero. A diferencia de la plantilla vieja, esta hoja escribe VALORES
/// (sin fórmulas): los totales ya vienen calculados por el motor de escenarios.
/// </summary>
public static class ScenariosSheetWriter
{
    private const string SheetName = "Escenarios";
    private const int HeaderRow = 4;
    private const int FirstDataRow = 5;

    /// <summary>Formato contable tomado verbatim del dump de referencia (columnas de valor de "Detalle").</summary>
    private const string AccountingFormat = "_ \"$\"* #,##0.00_ ;_ \"$\"* \\-#,##0.00_ ;_ \"$\"* \"-\"??_ ;_ @_ ";

    /// <summary>Fill tenue (verde de marca) para resaltar el escenario de mayor ahorro.</summary>
    private static readonly XLColor HighlightFill = XLColor.FromHtml(ExcelStyles.HeaderHex);

    /// <summary>Typo histórico de la plantilla vieja ("Depuraciión") — nunca se replica; siempre "Depuración".</summary>
    private const string HistoricTypo = "Depuraciión";
    private const string TypoFix = "Depuración";

    public static void Write(XLWorkbook wb, double paygBaselineMonthly,
        IReadOnlyList<ScenarioLineV3> baselineLines, IReadOnlyList<ScenarioV3> scenarios)
    {
        var ws = wb.Worksheets.Add(SheetName);

        // --- Bloques: base (col B/C) + uno de 2 columnas por escenario, separados por 1 col angosta ---
        // labelCol/valueCol de cada bloque: base=2/3, luego +3 por cada escenario (D/G/J/... son spacers).
        var blockStartCols = new List<int> { 2 };
        for (var i = 0; i < scenarios.Count; i++)
            blockStartCols.Add(blockStartCols[^1] + 3);

        // --- Fila 4: headers combinados ---
        WriteMergedHeader(ws, blockStartCols[0], "Detalle de los recursos");
        for (var i = 0; i < scenarios.Count; i++)
        {
            var sc = scenarios[i];
            WriteMergedHeader(ws, blockStartCols[i + 1], $"Escenario #{sc.Number} — {sc.Name}");
        }

        // --- Filas de breakdown ---
        var maxLines = Math.Max(baselineLines.Count, scenarios.Count == 0 ? 0 : scenarios.Max(s => s.Breakdown.Count));
        for (var line = 0; line < maxLines; line++)
        {
            var row = FirstDataRow + line;
            WriteLine(ws, blockStartCols[0], row, line < baselineLines.Count ? baselineLines[line] : null);
            for (var i = 0; i < scenarios.Count; i++)
            {
                var breakdown = scenarios[i].Breakdown;
                WriteLine(ws, blockStartCols[i + 1], row, line < breakdown.Count ? breakdown[line] : null);
            }
        }

        // --- Bloque de totales (2 filas de separación tras el último breakdown) ---
        var totalsStartRow = FirstDataRow + maxLines + 1;
        var bestScenarioNumber = scenarios.Count == 0
            ? (int?)null
            : scenarios.OrderByDescending(s => s.SavingsMonthly).First().Number;

        WriteTotalsBlock(ws, blockStartCols[0], totalsStartRow, "Total mensual", "Total anual",
            "Ahorro mensual", "Ahorro anual", "% ahorro",
            totalMonthly: paygBaselineMonthly, totalAnnual: paygBaselineMonthly * 12,
            savingsMonthly: null, savingsAnnual: null, savingsPct: null, highlight: false);

        for (var i = 0; i < scenarios.Count; i++)
        {
            var sc = scenarios[i];
            WriteTotalsBlock(ws, blockStartCols[i + 1], totalsStartRow, "Total mensual", "Total anual",
                "Ahorro mensual", "Ahorro anual", "% ahorro",
                totalMonthly: sc.TotalMonthly, totalAnnual: sc.TotalAnnual,
                savingsMonthly: sc.SavingsMonthly, savingsAnnual: sc.SavingsAnnual, savingsPct: sc.SavingsPct,
                highlight: sc.Number == bestScenarioNumber);
        }

        // --- Anchos de columna (labels anchos, valores angostos, spacers angostos) ---
        foreach (var start in blockStartCols)
        {
            ws.Column(start).Width = 32.2;
            ws.Column(start + 1).Width = 12.0;
        }
        for (var i = 0; i < scenarios.Count; i++)
            ws.Column(blockStartCols[i] + 2).Width = 5.0; // columna espaciadora angosta
    }

    private static void WriteMergedHeader(IXLWorksheet ws, int labelCol, string text)
    {
        var range = ws.Range(HeaderRow, labelCol, HeaderRow, labelCol + 1);
        range.Merge();
        var cell = ws.Cell(HeaderRow, labelCol);
        cell.SetValue(FixTypo(text));
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontColor = XLColor.FromHtml("#FFFFFF");
        cell.Style.Fill.PatternType = XLFillPatternValues.Solid;
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml(ExcelStyles.HeaderHex);
    }

    private static void WriteLine(IXLWorksheet ws, int labelCol, int row, ScenarioLineV3? line)
    {
        if (line is null) return;
        ws.Cell(row, labelCol).SetValue(FixTypo(line.Label));
        var valueCell = ws.Cell(row, labelCol + 1);
        valueCell.SetValue(line.MonthlyCost);
        valueCell.Style.NumberFormat.Format = AccountingFormat;
    }

    private static void WriteTotalsBlock(IXLWorksheet ws, int labelCol, int startRow,
        string totalMonthlyLabel, string totalAnnualLabel, string savingsMonthlyLabel, string savingsAnnualLabel, string savingsPctLabel,
        double totalMonthly, double totalAnnual, double? savingsMonthly, double? savingsAnnual, double? savingsPct, bool highlight)
    {
        WriteMoneyRow(ws, labelCol, startRow, totalMonthlyLabel, totalMonthly, highlight);
        WriteMoneyRow(ws, labelCol, startRow + 1, totalAnnualLabel, totalAnnual, highlight);
        if (savingsMonthly is not null)
            WriteMoneyRow(ws, labelCol, startRow + 2, savingsMonthlyLabel, savingsMonthly.Value, highlight);
        if (savingsAnnual is not null)
            WriteMoneyRow(ws, labelCol, startRow + 3, savingsAnnualLabel, savingsAnnual.Value, highlight);
        if (savingsPct is not null)
        {
            var row = startRow + 4;
            ws.Cell(row, labelCol).SetValue(savingsPctLabel);
            var valueCell = ws.Cell(row, labelCol + 1);
            valueCell.SetValue(savingsPct.Value);
            valueCell.Style.NumberFormat.Format = ExcelStyles.Percent;
            if (highlight) ApplyHighlight(ws, labelCol, row);
        }
    }

    private static void WriteMoneyRow(IXLWorksheet ws, int labelCol, int row, string label, double value, bool highlight)
    {
        ws.Cell(row, labelCol).SetValue(label);
        var valueCell = ws.Cell(row, labelCol + 1);
        valueCell.SetValue(value);
        valueCell.Style.NumberFormat.Format = AccountingFormat;
        if (highlight) ApplyHighlight(ws, labelCol, row);
    }

    /// <summary>Fill tenue en label+valor de la fila para el escenario de mayor ahorro.</summary>
    private static void ApplyHighlight(IXLWorksheet ws, int labelCol, int row)
    {
        ws.Cell(row, labelCol).Style.Fill.PatternType = XLFillPatternValues.Solid;
        ws.Cell(row, labelCol).Style.Fill.BackgroundColor = HighlightFill;
        ws.Cell(row, labelCol + 1).Style.Fill.PatternType = XLFillPatternValues.Solid;
        ws.Cell(row, labelCol + 1).Style.Fill.BackgroundColor = HighlightFill;
    }

    /// <summary>Corrige el typo histórico de la plantilla vieja ("Depuraciión" -&gt; "Depuración") si aparece.</summary>
    private static string FixTypo(string text) => text.Replace(HistoricTypo, TypoFix);
}
