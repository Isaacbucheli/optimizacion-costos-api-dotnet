using System.Text;
using ClosedXML.Excel;
using OptimizacionCostos.Api.Features.Reports.ExcelV3;

namespace OptimizacionCostos.Api.Features.Optimization;

/// <summary>Datos del barrido que encabezan el workbook (los arma el controller desde el servicio).</summary>
public sealed record OptExcelScanInfo(string ClientName, DateTime ScanDate, int SubscriptionsScanned);

/// <summary>
/// Exportador Excel de Oportunidades de Optimización: hoja Resumen + una hoja por categoría con
/// datos. Puro (sin BD): recibe los hallazgos ya filtrados por estado. Mismo look del Excel de
/// costos v3 (ExcelStyles). `includedStates` solo se muestra en el Resumen (vacío = "Todos").
/// </summary>
public interface IOptimizationExcelExporter
{
    ExcelV3Result Generate(OptExcelScanInfo scan,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> findings, IReadOnlyList<string> includedStates);
}

public sealed class OptimizationExcelExporter : IOptimizationExcelExporter
{
    private static readonly string[] SheetHeaders =
    [
        "Recomendación", "Recurso", "Grupo de recursos", "Suscripción", "Región",
        "Detalle", "Ahorro estimado/mes (USD)", "Severidad", "Estado", "Notas",
    ];

    public ExcelV3Result Generate(OptExcelScanInfo scan,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> findings, IReadOnlyList<string> includedStates)
    {
        using var wb = new XLWorkbook();
        wb.Properties.Title = $"{scan.ClientName} — Oportunidades de Optimización Azure";
        wb.Properties.Author = "Business IT";

        WriteSummary(wb, scan, findings, includedStates);

        foreach (var group in OptimizationExcelLabels.GroupOrder)
        {
            var rows = findings
                .Where(f => OptimizationExcelLabels.CheckGroup(Text(f, "check_id")) == group)
                .OrderByDescending(Savings)
                .ToList();
            if (rows.Count == 0) continue;
            WriteGroupSheet(wb, group, rows);
        }

        foreach (var ws in wb.Worksheets) ws.Visibility = XLWorksheetVisibility.Visible;
        wb.Worksheet(1).SetTabActive();

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        var fileName = $"oportunidades-optimizacion-{Sanitize(scan.ClientName)}-{scan.ScanDate:yyyyMMdd}.xlsx";
        return new ExcelV3Result(stream.ToArray(), fileName);
    }

    private static void WriteSummary(XLWorkbook wb, OptExcelScanInfo scan,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> findings, IReadOnlyList<string> includedStates)
    {
        var ws = wb.Worksheets.Add("Resumen");
        ws.Style.Font.FontName = "Arial";
        ws.Style.Font.FontSize = 10;

        ws.Cell(1, 1).Value = $"{scan.ClientName} — Oportunidades de Optimización Azure";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;

        var statesLabel = includedStates.Count == 0
            ? "Todos"
            : string.Join(", ", includedStates.Select(OptimizationExcelLabels.StateLabel));
        ws.Cell(2, 1).Value =
            $"Barrido del {scan.ScanDate:yyyy-MM-dd} · {scan.SubscriptionsScanned} suscripciones analizadas · Estados incluidos: {statesLabel}";
        ws.Cell(3, 1).Value = $"Generado el {DateTime.Now:yyyy-MM-dd} · Cifras mensuales estimadas en USD";
        ws.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml("#666666");
        ws.Cell(3, 1).Style.Font.FontColor = XLColor.FromHtml("#666666");

        // KPIs
        int Sev(string s) => findings.Count(f => Text(f, "severity") == s);
        var r = 5;
        ws.Cell(r, 1).Value = "Hallazgos";
        ws.Cell(r, 2).Value = findings.Count;
        ws.Cell(r + 1, 1).Value = "Ahorro estimado/mes";
        ws.Cell(r + 1, 2).Value = findings.Sum(Savings);
        ws.Cell(r + 1, 2).Style.NumberFormat.Format = ExcelStyles.Money;
        ws.Cell(r + 2, 1).Value = "Severidad Alta / Media / Baja";
        ws.Cell(r + 2, 2).Value = $"{Sev("high")} / {Sev("medium")} / {Sev("low")}";
        ws.Range(r, 1, r + 2, 1).Style.Font.Bold = true;

        // Tabla: ahorro por categoría
        r += 4;
        ws.Cell(r, 1).Value = "Ahorro por categoría";
        ws.Cell(r, 1).Style.Font.Bold = true;
        r++;
        ws.Cell(r, 1).Value = "Categoría"; ws.Cell(r, 2).Value = "Hallazgos"; ws.Cell(r, 3).Value = "Ahorro/mes";
        ExcelStyles.HeaderRow(ws, r, 3);
        foreach (var group in OptimizationExcelLabels.GroupOrder)
        {
            var rows = findings.Where(f => OptimizationExcelLabels.CheckGroup(Text(f, "check_id")) == group).ToList();
            if (rows.Count == 0) continue;
            r++;
            ws.Cell(r, 1).Value = group;
            ws.Cell(r, 2).Value = rows.Count;
            ws.Cell(r, 3).Value = rows.Sum(Savings);
            ws.Cell(r, 3).Style.NumberFormat.Format = ExcelStyles.Money;
        }

        // Tabla: por recomendación (check)
        r += 2;
        ws.Cell(r, 1).Value = "Por recomendación";
        ws.Cell(r, 1).Style.Font.Bold = true;
        r++;
        ws.Cell(r, 1).Value = "Recomendación"; ws.Cell(r, 2).Value = "Hallazgos"; ws.Cell(r, 3).Value = "Ahorro/mes";
        ExcelStyles.HeaderRow(ws, r, 3);
        foreach (var byCheck in findings
            .GroupBy(f => Text(f, "check_id"))
            .OrderByDescending(g => g.Sum(Savings)))
        {
            r++;
            ws.Cell(r, 1).Value = OptimizationExcelLabels.CheckTitle(byCheck.Key);
            ws.Cell(r, 2).Value = byCheck.Count();
            ws.Cell(r, 3).Value = byCheck.Sum(Savings);
            ws.Cell(r, 3).Style.NumberFormat.Format = ExcelStyles.Money;
        }

        // Nota metodológica
        r += 2;
        ws.Cell(r, 1).Value =
            "Metodología: el ahorro mensual es ESTIMADO con precios públicos de Azure (Retail Prices API). " +
            "El barrido es de solo lectura sobre el tenant (Azure Resource Graph) y tiene carácter informativo; " +
            "no constituye compromiso comercial.";
        ws.Cell(r, 1).Style.Font.Italic = true;
        ws.Cell(r, 1).Style.Font.FontColor = XLColor.FromHtml("#666666");

        ws.Columns().AdjustToContents();
        foreach (var col in ws.ColumnsUsed()) if (col.Width > 80) col.Width = 80;
    }

    private static void WriteGroupSheet(XLWorkbook wb, string group, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var ws = wb.Worksheets.Add(group);
        ws.Style.Font.FontName = "Arial";
        ws.Style.Font.FontSize = 10;

        for (var c = 0; c < SheetHeaders.Length; c++) ws.Cell(1, c + 1).Value = SheetHeaders[c];
        ExcelStyles.Header(ws, SheetHeaders.Length);

        var r = 2;
        foreach (var f in rows)
        {
            ws.Cell(r, 1).Value = OptimizationExcelLabels.CheckTitle(Text(f, "check_id"));
            ws.Cell(r, 2).Value = Text(f, "resource_name");
            ws.Cell(r, 3).Value = OptimizationExcelLabels.ParseResourceGroup(Text(f, "azure_resource_id"));
            ws.Cell(r, 4).Value = Text(f, "subscription_id");
            ws.Cell(r, 5).Value = Text(f, "region");
            ws.Cell(r, 6).Value = OptimizationExcelLabels.DetailText(f.TryGetValue("details", out var d) ? d : null);
            var s = Savings(f);
            if (s > 0)
            {
                ws.Cell(r, 7).Value = s;
                ws.Cell(r, 7).Style.NumberFormat.Format = ExcelStyles.Money;
            }
            ws.Cell(r, 8).Value = OptimizationExcelLabels.SeverityLabel(Text(f, "severity"));
            ws.Cell(r, 9).Value = OptimizationExcelLabels.StateLabel(Text(f, "state"));
            ws.Cell(r, 10).Value = Text(f, "notes");
            ExcelStyles.DataRow(ws.Row(r), zebra: r % 2 == 0);
            r++;
        }

        ws.Cell(r, 1).Value = $"TOTAL ({rows.Count})";
        ws.Cell(r, 7).Value = rows.Sum(Savings);
        ws.Cell(r, 7).Style.NumberFormat.Format = ExcelStyles.Money;
        ExcelStyles.SubtotalRow(ws.Row(r), grand: true);

        ws.Columns().AdjustToContents();
        foreach (var col in ws.ColumnsUsed()) if (col.Width > 60) col.Width = 60;
    }

    private static string Text(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

    private static double Savings(IReadOnlyDictionary<string, object?> row) =>
        row.TryGetValue("estimated_monthly_savings", out var v) && v is not null && v is not DBNull
            ? Convert.ToDouble(v) : 0;

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(ch == ' ' || invalid.Contains(ch) ? '-' : ch);
        return sb.ToString();
    }
}
