using System.Text;
using ClosedXML.Excel;

namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>Resultado de la generación v3: bytes del .xlsx + nombre de archivo sugerido.</summary>
public sealed record ExcelV3Result(byte[] Bytes, string FileName);

/// <summary>
/// Exportador Excel v3 (código, no plantilla): arma el workbook completo a partir de
/// <see cref="ICostExcelDataSourceV3"/> + <see cref="SheetCatalog"/>/<see cref="SheetWriter"/>/
/// <see cref="SummarySheetWriter"/>/<see cref="ScenariosSheetWriter"/>. NO recalcula costos: solo
/// pinta lo que ya viene calculado (y, si se pide margen, lo escala uniformemente).
/// </summary>
public interface ICostExcelExporterV3
{
    Task<ExcelV3Result> GenerateAsync(int analysisId, decimal? marginPct, CancellationToken ct);
}

/// <summary>service_key crudo (fila de "results") → clave visible que ya tiene hoja propia en
/// SheetCatalog.ServiceSheets() o que se arma con otro criterio (discos se parten en dos hojas
/// filtradas por SplitDiskRows). Cualquier service_key que NO esté aquí necesita una hoja genérica.</summary>
file static class CoveredServiceKeys
{
    public static readonly HashSet<string> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        "vms", "sql_vm", "disks", "sql", "sql_managed_instance", "appservice", "mysql", "cosmos", "redis", "public_ip",
    };
}

public sealed class CodeCostExcelExporter(ICostExcelDataSourceV3 data) : ICostExcelExporterV3
{
    public async Task<ExcelV3Result> GenerateAsync(int analysisId, decimal? marginPct, CancellationToken ct)
    {
        var loaded = await data.LoadAsync(analysisId, ct);
        var (rowsByService, scenarios, baselineLines, paygBaselineMonthly) = ApplyMargin(loaded, marginPct);

        using var wb = new XLWorkbook();

        var results = rowsByService.TryGetValue("results", out var r) ? r : [];

        // --- Resumen Ejecutivo (primera hoja, activa) ---
        var services = BuildServiceSummaries(rowsByService, results);
        var (variableCount, manualCount) = CountPricingStatuses(results);
        SummarySheetWriter.Write(wb, loaded.ClientName, loaded.AnalysisName, loaded.CreatedAt, DateTime.Now,
            services, scenarios, variableCount, manualCount);

        // --- Escenarios ---
        ScenariosSheetWriter.Write(wb, paygBaselineMonthly, baselineLines, scenarios);

        // --- Hojas de servicio del catálogo (solo las que tienen filas) ---
        foreach (var (dataKey, spec) in SheetCatalog.ServiceSheets())
        {
            if (!rowsByService.TryGetValue(dataKey, out var rows) || rows.Count == 0) continue;
            SheetWriter.Write(wb, spec, rows);
        }

        // --- Hojas genéricas para service_key de "results" sin spec propio ---
        foreach (var (serviceKey, displayName, rows) in UncoveredServiceGroups(results))
        {
            if (rows.Count == 0) continue;
            SheetWriter.Write(wb, SheetCatalog.GenericServiceSheet(displayName), rows);
        }

        // --- Detalle de recursos (todas las filas de "results") ---
        if (results.Count > 0)
            SheetWriter.Write(wb, SheetCatalog.ResourceDetailSheet(), results);

        // Ninguna hoja oculta.
        foreach (var ws in wb.Worksheets)
            ws.Visibility = XLWorksheetVisibility.Visible;

        // Resumen Ejecutivo activa al abrir.
        wb.Worksheet(1).SetTabActive();

        using var stream = new MemoryStream();
        wb.SaveAs(stream);

        var fileName = $"Optimizacion-Costos-{Sanitize(loaded.ClientName)}-{DateTime.Now:yyyyMMdd}.xlsx";
        return new ExcelV3Result(stream.ToArray(), fileName);
    }

    /// <summary>
    /// Si hay margen (&gt; 0): escala in-place TODAS las listas de filas (MarginApplier.ApplyInPlace)
    /// y recrea escenarios/baseline con montos ×(1+m/100) — los porcentajes (SavingsPct) quedan
    /// intactos porque son invariantes bajo un margen uniforme (mismo factor en PAYG y RI).
    /// </summary>
    private static (Dictionary<string, List<Dictionary<string, object?>>> Rows, IReadOnlyList<ScenarioV3> Scenarios,
        IReadOnlyList<ScenarioLineV3> BaselineLines, double PaygBaselineMonthly) ApplyMargin(ExcelV3Data loaded, decimal? marginPct)
    {
        if (marginPct is not > 0) return (loaded.RowsByService, loaded.Scenarios, loaded.BaselineLines, loaded.PaygBaselineMonthly);

        var m = marginPct.Value;
        // Distinct por referencia: algunas listas comparten las MISMAS instancias de fila (ej.
        // "results" reutiliza los mismos diccionarios que "vms"/"disks_*"/etc.) — aplicar el margen
        // por lista, sin deduplicar, lo aplicaría dos veces a esas filas compartidas.
        var uniqueRows = loaded.RowsByService.Values
            .SelectMany(rows => rows)
            .Distinct<Dictionary<string, object?>>(ReferenceEqualityComparer.Instance);
        MarginApplier.ApplyInPlace(uniqueRows, m);

        var scenarios = loaded.Scenarios.Select(sc => new ScenarioV3(
            sc.Number, sc.Name, sc.Description,
            MarginApplier.Apply(sc.TotalMonthly, m), MarginApplier.Apply(sc.TotalAnnual, m),
            MarginApplier.Apply(sc.SavingsMonthly, m), MarginApplier.Apply(sc.SavingsAnnual, m),
            sc.SavingsPct,
            sc.Breakdown.Select(l => new ScenarioLineV3(l.Label, MarginApplier.Apply(l.MonthlyCost, m), l.Note)).ToList()))
            .ToList();

        var baselineLines = loaded.BaselineLines
            .Select(l => new ScenarioLineV3(l.Label, MarginApplier.Apply(l.MonthlyCost, m), l.Note))
            .ToList();

        var paygBaselineMonthly = MarginApplier.Apply(loaded.PaygBaselineMonthly, m);

        return (loaded.RowsByService, scenarios, baselineLines, paygBaselineMonthly);
    }

    /// <summary>Agrupa "results" por service_key VISIBLE (aplicando CostLabels.VisibleServiceKey, que pliega
    /// "sql_vm" en "vms"), mismo criterio que la etiqueta de baseline. Una fila por servicio con datos,
    /// usando la etiqueta legible (display_name) cuando existe. Prioriza el display_name de filas cuyo
    /// service_key matches exactamente el visible_key (ej. una fila con service_key="vms" sobre una
    /// con service_key="sql_vm" dentro del grupo "vms").</summary>
    private static List<(string ServiceLabel, IReadOnlyList<Dictionary<string, object?>> Rows)> BuildServiceSummaries(
        Dictionary<string, List<Dictionary<string, object?>>> rowsByService,
        List<Dictionary<string, object?>> results)
    {
        var groups = new Dictionary<string, (string Label, List<Dictionary<string, object?>> Rows)>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in results)
        {
            var rawKey = Text(Get(row, "service_key"));
            if (string.IsNullOrEmpty(rawKey)) continue;

            var visibleKey = CostLabels.VisibleServiceKey(rawKey);
            if (!seen.Add(visibleKey)) continue;

            var label = Text(Get(row, "display_name"));
            var rowsForVisibleKey = results.Where(x =>
            {
                var xRaw = Text(Get(x, "service_key"));
                return string.Equals(CostLabels.VisibleServiceKey(xRaw), visibleKey, StringComparison.OrdinalIgnoreCase);
            }).ToList();

            // Si aún no hay etiqueta (o es vacía), intenta prioritariamente filas cuyo service_key matches
            // exactamente el visible_key (para que "vms" use display_name de una fila vms, no de sql_vm).
            if (string.IsNullOrEmpty(label))
            {
                var matchingRowLabel = rowsForVisibleKey.FirstOrDefault(x =>
                    string.Equals(Text(Get(x, "service_key")), visibleKey, StringComparison.OrdinalIgnoreCase));
                if (matchingRowLabel is not null)
                    label = Text(Get(matchingRowLabel, "display_name"));
            }

            // Si sigue siendo vacía, usa el visible_key como fallback.
            if (string.IsNullOrEmpty(label))
                label = visibleKey;

            groups[visibleKey] = (label, rowsForVisibleKey);
        }

        return groups.Values.Select(g => (g.Label, (IReadOnlyList<Dictionary<string, object?>>)g.Rows)).ToList();
    }

    /// <summary>Cuenta filas de "results" con calculation_status variable_pricing / manual_required
    /// para las viñetas dinámicas de metodología del Resumen.</summary>
    private static (int VariableCount, int ManualCount) CountPricingStatuses(List<Dictionary<string, object?>> results)
    {
        int variable = 0, manual = 0;
        foreach (var row in results)
        {
            var status = Text(Get(row, "calculation_status"));
            if (string.Equals(status, "variable_pricing", StringComparison.OrdinalIgnoreCase)) variable++;
            else if (string.Equals(status, "manual_required", StringComparison.OrdinalIgnoreCase)) manual++;
        }
        return (variable, manual);
    }

    /// <summary>service_key de "results" que no tienen hoja propia en SheetCatalog.ServiceSheets()
    /// (ej. synapse_dw): agrupados para pintarse con GenericServiceSheet.</summary>
    private static List<(string ServiceKey, string DisplayName, List<Dictionary<string, object?>> Rows)> UncoveredServiceGroups(
        List<Dictionary<string, object?>> results)
    {
        var groups = new Dictionary<string, (string DisplayName, List<Dictionary<string, object?>> Rows)>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in results)
        {
            var key = Text(Get(row, "service_key"));
            if (string.IsNullOrEmpty(key) || CoveredServiceKeys.Values.Contains(key)) continue;

            if (!groups.TryGetValue(key, out var entry))
            {
                entry = (Text(Get(row, "display_name"), key), []);
                groups[key] = entry;
            }
            entry.Rows.Add(row);
        }

        return groups.Select(kv => (kv.Key, kv.Value.DisplayName, kv.Value.Rows)).ToList();
    }

    /// <summary>Reemplaza caracteres inválidos de nombre de archivo Y espacios por "-" (ej. "Cliente Prueba" -> "Cliente-Prueba").</summary>
    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(ch == ' ' || invalid.Contains(ch) ? '-' : ch);
        return sb.ToString();
    }

    private static object? Get(IDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) ? v : null;

    private static string Text(object? value, string @default = "") =>
        value is null or DBNull ? @default : (value.ToString() ?? @default);
}
