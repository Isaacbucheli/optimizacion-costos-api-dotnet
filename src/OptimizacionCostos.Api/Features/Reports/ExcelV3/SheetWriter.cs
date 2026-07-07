using ClosedXML.Excel;

namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>
/// Escritor genérico de hojas para el exportador Excel v3 (código, no plantilla). A partir de un
/// <see cref="SheetSpec"/> declarativo y las filas (diccionarios canónicos case-insensitive), escribe
/// header + filas de datos con zebra + (opcional) una única fila TOTAL con fórmulas vivas.
/// Los Ahorros $/% por fila y el TOTAL se escriben como FÓRMULAS intra-hoja (spec §3.6): Ahorro por
/// fila = IF(AND(Elegible="Sí", RI&lt;&gt;""), PAYG−RI, 0); TOTAL PAYG/Ahorro = SUBTOTAL(9,…) (sensible
/// al filtro) y "Total optimizado" = PAYG−Ahorro sobre la propia fila TOTAL. La matemática, por tanto,
/// se computa en la hoja (no aquí). <see cref="ComparableTotalsCalculator"/> sigue siendo la fuente de
/// verdad solo para el Resumen (valores por servicio) y para la regla IsEligible de la columna
/// "Elegible a RI" en <see cref="SheetCatalog"/>.
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

        // --- Fórmulas vivas por fila: Ahorro 1A/3A $ y % (spec §3.6) ---
        // Solo si la hoja tiene columna "Elegible a RI" (Task 1) y al menos PAYG+RI1 o PAYG+RI3.
        var moneyRoleCols = spec.Columns
            .Select((c, idx) => (Col: idx + 1, Spec: c))
            .Where(x => x.Spec.Role != MoneyRole.None)
            .ToList();
        var hasComparableRoles = moneyRoleCols.Any(x => x.Spec.Role is MoneyRole.Payg or MoneyRole.Ri1 or MoneyRole.Ri3);

        var eligCol = FindColumn(spec, c => c.Kind == ColKind.Eligibility);
        var paygCol = FindColumn(spec, c => c.Role == MoneyRole.Payg);
        var ri1Col = FindColumn(spec, c => c.Role == MoneyRole.Ri1);
        var ri3Col = FindColumn(spec, c => c.Role == MoneyRole.Ri3);
        var sav1UsdCol = FindColumn(spec, c => c.Role == MoneyRole.Savings1Usd);
        var sav3UsdCol = FindColumn(spec, c => c.Role == MoneyRole.Savings3Usd);
        var sav1PctCol = FindColumn(spec, c => c.Role == MoneyRole.Savings1Pct);
        var sav3PctCol = FindColumn(spec, c => c.Role == MoneyRole.Savings3Pct);

        if (rows.Count > 0 && eligCol is not null && paygCol is not null)
        {
            var eligLetter = ColLetter(eligCol.Value);
            var paygLetter = ColLetter(paygCol.Value);
            var ri1Letter = ri1Col is { } r1 ? ColLetter(r1) : null;
            var ri3Letter = ri3Col is { } r3 ? ColLetter(r3) : null;
            var sav1Letter = sav1UsdCol is { } s1 ? ColLetter(s1) : null;
            var sav3Letter = sav3UsdCol is { } s3 ? ColLetter(s3) : null;

            for (var i = 0; i < rows.Count; i++)
            {
                var r = i + 2; // fila 1 = header

                if (sav1UsdCol is { } c1 && ri1Letter is not null)
                {
                    var cell = ws.Cell(r, c1);
                    cell.FormulaA1 = $"=IF(AND({eligLetter}{r}=\"Sí\",{ri1Letter}{r}<>\"\"),{paygLetter}{r}-{ri1Letter}{r},0)";
                    ApplyNumberFormat(cell, ColKind.Money);
                }
                if (sav3UsdCol is { } c3 && ri3Letter is not null)
                {
                    var cell = ws.Cell(r, c3);
                    cell.FormulaA1 = $"=IF(AND({eligLetter}{r}=\"Sí\",{ri3Letter}{r}<>\"\"),{paygLetter}{r}-{ri3Letter}{r},0)";
                    ApplyNumberFormat(cell, ColKind.Money);
                }
                if (sav1PctCol is { } p1 && sav1Letter is not null)
                {
                    var cell = ws.Cell(r, p1);
                    cell.FormulaA1 = $"=IF({paygLetter}{r}>0,{sav1Letter}{r}/{paygLetter}{r},0)";
                    ApplyNumberFormat(cell, ColKind.Percent);
                }
                if (sav3PctCol is { } p3 && sav3Letter is not null)
                {
                    var cell = ws.Cell(r, p3);
                    cell.FormulaA1 = $"=IF({paygLetter}{r}>0,{sav3Letter}{r}/{paygLetter}{r},0)";
                    ApplyNumberFormat(cell, ColKind.Percent);
                }
            }
        }

        // --- Fila TOTAL (una sola), como fórmulas intra-hoja SENSIBLES AL FILTRO ---
        // SUBTOTAL(9,...) ignora las filas ocultas por autofiltro: si el usuario filtra la tabla,
        // el TOTAL refleja SOLO lo visible. El Ahorro por fila ya vale 0 en los no-elegibles, así que
        // "Total optimizado = PAYG − Ahorro" nunca se infla, aun filtrando. Sin filas de desglose por
        // elegibilidad (decisión del usuario 2026-07-07): la columna "Elegible a RI" ya lo indica por
        // recurso, y el desglose no puede ser sensible al filtro sin fórmulas frágiles.
        if (spec.ComparableTotals && hasComparableRoles && rows.Count > 0)
        {
            var totalRow = lastDataRow + 1;
            ws.Cell(totalRow, 1).SetValue($"TOTAL ({rows.Count})");

            var paygLetterT = paygCol is { } pc ? ColLetter(pc) : null;
            var sav1LetterT = sav1UsdCol is { } s1c ? ColLetter(s1c) : null;
            var sav3LetterT = sav3UsdCol is { } s3c ? ColLetter(s3c) : null;

            foreach (var (col, colSpec) in moneyRoleCols)
            {
                var letter = ColLetter(col);
                var colRange = $"{letter}2:{letter}{lastDataRow}";
                // Sin `default`/`else` a propósito: si a la hoja le faltara una columna de rol
                // (spec incompleta), los guards `when …` dejan la celda del TOTAL en blanco en vez de
                // emitir una fórmula con referencia rota. Silencio = por diseño, no un descuido.
                switch (colSpec.Role)
                {
                    // PAYG y Ahorros $: suma sensible al filtro.
                    case MoneyRole.Payg:
                    case MoneyRole.Savings1Usd:
                    case MoneyRole.Savings3Usd:
                        SetFormula(ws.Cell(totalRow, col), colSpec, $"=SUBTOTAL(9,{colRange})");
                        break;

                    // RI 1A/3A del TOTAL = "Total optimizado" = PAYG − Ahorro (referencia la propia fila TOTAL).
                    case MoneyRole.Ri1 when paygLetterT is not null && sav1LetterT is not null:
                        SetFormula(ws.Cell(totalRow, col), colSpec, $"={paygLetterT}{totalRow}-{sav1LetterT}{totalRow}");
                        break;
                    case MoneyRole.Ri3 when paygLetterT is not null && sav3LetterT is not null:
                        SetFormula(ws.Cell(totalRow, col), colSpec, $"={paygLetterT}{totalRow}-{sav3LetterT}{totalRow}");
                        break;

                    // Ahorro %: ahorro/PAYG sobre los totales ya sensibles al filtro.
                    case MoneyRole.Savings1Pct when paygLetterT is not null && sav1LetterT is not null:
                        SetFormula(ws.Cell(totalRow, col), colSpec,
                            $"=IF({paygLetterT}{totalRow}>0,{sav1LetterT}{totalRow}/{paygLetterT}{totalRow},0)");
                        break;
                    case MoneyRole.Savings3Pct when paygLetterT is not null && sav3LetterT is not null:
                        SetFormula(ws.Cell(totalRow, col), colSpec,
                            $"=IF({paygLetterT}{totalRow}>0,{sav3LetterT}{totalRow}/{paygLetterT}{totalRow},0)");
                        break;
                }
            }

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

    /// <summary>Índice de columna (1-based) de la primera <see cref="ColumnSpec"/> que cumple el
    /// predicado, o null si la hoja no tiene esa columna (p.ej. una hoja sin RI3A).</summary>
    private static int? FindColumn(SheetSpec spec, Func<ColumnSpec, bool> predicate)
    {
        for (var i = 0; i < spec.Columns.Count; i++)
            if (predicate(spec.Columns[i])) return i + 1;
        return null;
    }

    /// <summary>Letra(s) de columna de Excel para un índice 1-based (1 -&gt; "A", 27 -&gt; "AA"), vía
    /// ClosedXML (mismo helper que usa la librería internamente para direcciones A1).</summary>
    private static string ColLetter(int col1Based) => XLHelper.GetColumnLetterFromNumber(col1Based);

    /// <summary>Escribe una fórmula viva en la celda del bloque de totales y conserva su formato numérico.</summary>
    private static void SetFormula(IXLCell cell, ColumnSpec colSpec, string formulaA1)
    {
        cell.FormulaA1 = formulaA1;
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
