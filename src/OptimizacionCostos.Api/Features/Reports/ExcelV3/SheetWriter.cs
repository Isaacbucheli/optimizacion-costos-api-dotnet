using ClosedXML.Excel;

namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>
/// Escritor genérico de hojas para el exportador Excel v3 (código, no plantilla). A partir de un
/// <see cref="SheetSpec"/> declarativo y las filas (diccionarios canónicos case-insensitive), escribe
/// header + filas de datos con zebra + (opcional) el bloque de 3 filas de totales comparables.
/// NO reimplementa la matemática de los totales: delega en <see cref="ComparableTotalsCalculator"/>.
/// </summary>
public static class SheetWriter
{
    private const int MaxAutoWidth = 55;

    public static IXLWorksheet Write(XLWorkbook wb, SheetSpec spec, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var ws = wb.Worksheets.Add(spec.Name);
        var colCount = spec.Columns.Count;

        // --- Header (fila 1) ---
        for (var col = 0; col < colCount; col++)
            ws.Cell(1, col + 1).SetValue(spec.Columns[col].Header);

        // --- Filas de datos ---
        for (var i = 0; i < rows.Count; i++)
        {
            var rowNum = i + 2; // fila 1 = header
            var data = rows[i];
            for (var col = 0; col < colCount; col++)
            {
                var colSpec = spec.Columns[col];
                var cell = ws.Cell(rowNum, col + 1);
                SetCellValue(cell, colSpec.Kind, colSpec.Extract(data));
            }
            ExcelStyles.DataRow(ws.Row(rowNum), zebra: (i % 2) == 1);
        }

        var lastDataRow = rows.Count + 1;

        // --- Bloque de totales comparables (si aplica) ---
        var moneyRoleCols = spec.Columns
            .Select((c, idx) => (Col: idx + 1, Spec: c))
            .Where(x => x.Spec.Role != MoneyRole.None)
            .ToList();
        var hasComparableRoles = moneyRoleCols.Any(x => x.Spec.Role is MoneyRole.Payg or MoneyRole.Ri1 or MoneyRole.Ri3);

        if (spec.ComparableTotals && hasComparableRoles && rows.Count > 0)
        {
            var totals = ComparableTotalsCalculator.Compute(rows.Select(ToTotalsInput).ToList());

            var eligibleRow = lastDataRow + 1;
            var notEligibleRow = lastDataRow + 2;
            var totalRow = lastDataRow + 3;

            ws.Cell(eligibleRow, 1).SetValue($"Subtotal elegible a RI ({totals.EligibleCount})");
            ws.Cell(notEligibleRow, 1).SetValue($"Subtotal no elegible ({totals.NotEligibleCount})");
            ws.Cell(totalRow, 1).SetValue($"TOTAL ({totals.RowCount})");

            foreach (var (col, colSpec) in moneyRoleCols)
            {
                WriteRoleValue(ws.Cell(eligibleRow, col), colSpec, EligibleValue(colSpec.Role, totals));
                WriteRoleValue(ws.Cell(notEligibleRow, col), colSpec, NotEligibleValue(colSpec.Role, totals));
                WriteRoleValue(ws.Cell(totalRow, col), colSpec, TotalValue(colSpec.Role, totals));
            }

            ExcelStyles.SubtotalRow(ws.Row(eligibleRow), grand: false);
            ExcelStyles.SubtotalRow(ws.Row(notEligibleRow), grand: false);
            ExcelStyles.SubtotalRow(ws.Row(totalRow), grand: true);
        }

        // --- Header (estilo, freeze, autofiltro) ---
        ExcelStyles.Header(ws, colCount);

        // --- Anchos de columna ---
        for (var col = 0; col < colCount; col++)
        {
            var colSpec = spec.Columns[col];
            if (colSpec.Width is { } w)
                ws.Column(col + 1).Width = w;
            else
            {
                ws.Column(col + 1).AdjustToContents();
                if (ws.Column(col + 1).Width > MaxAutoWidth)
                    ws.Column(col + 1).Width = MaxAutoWidth;
            }
        }

        return ws;
    }

    /// <summary>Valor de la fila "Subtotal elegible a RI" para el rol dado (null = celda vacía).</summary>
    private static double? EligibleValue(MoneyRole role, ComparableTotals t) => role switch
    {
        MoneyRole.Payg => t.PaygEligible,
        MoneyRole.Ri1 => t.Ri1Eligible,
        MoneyRole.Ri3 => t.Ri3Eligible,
        MoneyRole.Savings1Usd => t.PaygEligible - t.Ri1Eligible,
        MoneyRole.Savings3Usd => t.PaygEligible - t.Ri3Eligible,
        MoneyRole.Savings1Pct => t.PaygEligible > 0 ? (t.PaygEligible - t.Ri1Eligible) / t.PaygEligible : 0,
        MoneyRole.Savings3Pct => t.PaygEligible > 0 ? (t.PaygEligible - t.Ri3Eligible) / t.PaygEligible : 0,
        _ => null,
    };

    /// <summary>Valor de la fila "Subtotal no elegible": paga PAYG en toda columna de dinero; ahorro vacío.</summary>
    private static double? NotEligibleValue(MoneyRole role, ComparableTotals t) => role switch
    {
        MoneyRole.Payg => t.PaygNotEligible,
        MoneyRole.Ri1 => t.PaygNotEligible,
        MoneyRole.Ri3 => t.PaygNotEligible,
        _ => null, // Savings* queda vacío
    };

    /// <summary>Valor de la fila TOTAL: comparables (RI de elegibles + PAYG de no elegibles) y ahorro real.</summary>
    private static double? TotalValue(MoneyRole role, ComparableTotals t) => role switch
    {
        MoneyRole.Payg => t.PaygTotal,
        MoneyRole.Ri1 => t.Total1,
        MoneyRole.Ri3 => t.Total3,
        MoneyRole.Savings1Usd => t.Savings1,
        MoneyRole.Savings3Usd => t.Savings3,
        MoneyRole.Savings1Pct => t.Savings1Pct,
        MoneyRole.Savings3Pct => t.Savings3Pct,
        _ => null,
    };

    /// <summary>Escribe un valor de rol en la celda del bloque de totales, o la deja vacía si es null.</summary>
    private static void WriteRoleValue(IXLCell cell, ColumnSpec colSpec, double? value)
    {
        if (value is null) return;
        cell.SetValue(value.Value);
        ApplyNumberFormat(cell, colSpec.Kind);
    }

    /// <summary>Escribe el valor de una celda de datos según el tipo de columna, con su formato numérico.</summary>
    private static void SetCellValue(IXLCell cell, ColKind kind, object? value)
    {
        if (value is null || value is DBNull)
        {
            cell.Clear(XLClearOptions.Contents);
            return;
        }
        switch (kind)
        {
            case ColKind.Text:
            case ColKind.Eligibility:
                cell.SetValue(value.ToString() ?? "");
                break;
            case ColKind.Money:
            case ColKind.Percent:
            case ColKind.Number:
            case ColKind.Hours:
                cell.SetValue(AsDouble(value));
                ApplyNumberFormat(cell, kind);
                break;
        }
    }

    private static void ApplyNumberFormat(IXLCell cell, ColKind kind)
    {
        cell.Style.NumberFormat.Format = kind switch
        {
            ColKind.Money => ExcelStyles.Money,
            ColKind.Percent => ExcelStyles.Percent,
            ColKind.Number or ColKind.Hours => ExcelStyles.Int,
            _ => cell.Style.NumberFormat.Format,
        };
    }

    /// <summary>
    /// Convierte una fila canónica (claves case-insensitive) al insumo mínimo de los totales
    /// comparables. Reutilizado por el Resumen. payg efectivo = payg_monthly ?? manual_monthly_cost
    /// ?? 0; ri_1y_monthly/ri_3y_monthly como double?; confirmado = ri_coverage == "confirmed"
    /// (case-insensitive).
    /// </summary>
    public static TotalsInput ToTotalsInput(IDictionary<string, object?> row)
    {
        var payg = AsDouble(GetOrNull(row, "payg_monthly")) ?? AsDouble(GetOrNull(row, "manual_monthly_cost")) ?? 0;
        var ri1 = AsDouble(GetOrNull(row, "ri_1y_monthly"));
        var ri3 = AsDouble(GetOrNull(row, "ri_3y_monthly"));
        var coverage = GetOrNull(row, "ri_coverage") as string;
        var confirmed = string.Equals(coverage, "confirmed", StringComparison.OrdinalIgnoreCase);
        return new TotalsInput(payg, ri1, ri3, confirmed);
    }

    private static object? GetOrNull(IDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) ? v : null;

    /// <summary>Convierte un valor proveniente de SQL (object?) a double, defensivamente.</summary>
    private static double? AsDouble(object? value)
    {
        if (value is null || value is DBNull) return null;
        return Convert.ToDouble(value);
    }
}
