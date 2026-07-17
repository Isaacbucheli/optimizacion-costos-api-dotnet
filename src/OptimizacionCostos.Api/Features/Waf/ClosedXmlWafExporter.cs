using System.Globalization;
using System.Linq;
using System.Reflection;
using ClosedXML.Excel;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Rellena la hoja "Resultados" de la plantilla BIT con ClosedXML (equivalente a openpyxl del
/// Python) y devuelve el .xlsx generado por ClosedXML tal cual.
///
/// HISTORIA (por qué YA NO se parcha el paquete): el port original replicaba
/// _restore_template_package del Python, que tomaba el sheet2.xml/styles.xml generados y los metía
/// dentro del paquete ORIGINAL de la plantilla para conservar imágenes/comentarios/pageSetup que
/// openpyxl descartaba. Ese hÍbrido era la causa del bug "Excel encontró un problema con el
/// contenido": ClosedXML escribe las cadenas como *shared strings* (índices a xl/sharedStrings.xml)
/// mientras que openpyxl las escribía *inline*. Al conservar el sharedStrings.xml de la plantilla
/// (36 cadenas) junto al sheet2.xml reindexado por ClosedXML (índices hasta ~64), las celdas de
/// datos apuntaban a entradas inexistentes → paquete corrupto. (El OpenXmlValidator no lo detecta:
/// un índice de shared string fuera de rango no viola el esquema.)
///
/// ClosedXML 0.105 preserva por sí solo lo que el restore intentaba proteger: imágenes (drawing de
/// la portada "Health Check" + media), comentarios threaded, legacyDrawing/VML, pageSetup,
/// pageMargins y extLst. Su salida es autoconsistente (sheet + styles + sharedStrings del mismo
/// paso), valida limpia y abre sin pedir reparación. Por eso se devuelve directamente y se eliminó
/// toda la cirugía de ZIP/XML. Único cambio cosmético frente al restore: se pierde el texto
/// *placeholder* de los comentarios legacy de la plantilla ("[Comentario]"), que no es dato del
/// cliente; los comentarios threaded reales se conservan intactos.
/// </summary>
public sealed class ClosedXmlWafExporter : IWafExcelExporter
{
    // Port de SECTION_HEADER_ROWS / PLACEHOLDERS_PER_SECTION / MAX_RESOURCES_LISTED.
    private static readonly IReadOnlyDictionary<int, int> SectionHeaderRows =
        new Dictionary<int, int> { [1] = 3, [2] = 7, [3] = 11, [4] = 15, [5] = 19 };
    private const int PlaceholdersPerSection = 3;
    private const int MaxResourcesListed = 10;

    private const string SheetName = "Resultados";

    public Task<byte[]> ExportAsync(
        int clientId, IReadOnlyList<WafExportRow> recommendations, CancellationToken ct = default)
    {
        // CPU/IO síncrono (ClosedXML). Se envuelve en Task para respetar la firma async.
        return Task.Run(() => Export(recommendations), ct);
    }

    private static byte[] Export(IReadOnlyList<WafExportRow> recommendations)
    {
        var templateBytes = LoadTemplateBytes();

        using var templateStream = new MemoryStream(templateBytes, writable: false);
        using var workbook = new XLWorkbook(templateStream);
        if (!workbook.Worksheets.TryGetWorksheet(SheetName, out var sheet))
            throw new InvalidOperationException("La plantilla WAF no contiene la hoja Resultados.");

        // by_pillar = {1..5: []}; pillar inválido/None -> 2 (port exacto).
        var byPillar = new Dictionary<int, List<WafExportRow>>();
        for (var p = 1; p <= 5; p++) byPillar[p] = [];
        foreach (var rec in recommendations)
        {
            var pillar = rec.PillarNumber ?? 2;
            if (!byPillar.ContainsKey(pillar)) pillar = 2;
            byPillar[pillar].Add(rec);
        }

        // sort estable por _sort_key dentro de cada pilar (snapshot de claves: no se puede
        // mutar el diccionario mientras se enumera su colección de claves).
        foreach (var p in byPillar.Keys.ToList())
            byPillar[p] = byPillar[p].OrderBy(SortKey, SortKeyComparer.Instance).ToList();

        var maxColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 12;

        // Iterar pilares en orden INVERSO (5,4,3,2,1) para no correr índices al insertar.
        foreach (var pillar in new[] { 5, 4, 3, 2, 1 })
        {
            var headerRow = SectionHeaderRows[pillar];
            var firstDataRow = headerRow + 1;
            var pillarRecs = byPillar[pillar];

            if (pillarRecs.Count > PlaceholdersPerSection)
            {
                var extraRows = pillarRecs.Count - PlaceholdersPerSection;
                var insertAt = headerRow + PlaceholdersPerSection + 1;
                // insert_rows(insert_at, amount=extra_rows): inserta arriba de insertAt.
                sheet.Row(insertAt).InsertRowsAbove(extraRows);
                for (var offset = 0; offset < extraRows; offset++)
                    CopyRowStyle(sheet, firstDataRow, insertAt + offset, maxColumn);
            }

            for (var offset = 0; offset < pillarRecs.Count; offset++)
                FillRow(sheet, firstDataRow + offset, pillarRecs[offset]);
            for (var offset = pillarRecs.Count; offset < PlaceholdersPerSection; offset++)
                ClearRow(sheet, firstDataRow + offset, maxColumn);
        }

        using var outStream = new MemoryStream();
        workbook.SaveAs(outStream);
        return outStream.ToArray();
    }

    // ---------- Carga de plantilla ----------

    /// <summary>
    /// Lee BIT_Matriz_Template_V1_2.xlsx. Como el .csproj no se modifica (no se marca el archivo
    /// como Content/EmbeddedResource), se busca en disco junto al ensamblado y en el árbol de
    /// código (Features/Waf/Templates). RIESGO: si el archivo no se copia al output en
    /// despliegue, hay que añadir &lt;Content Include="Features\Waf\Templates\*.xlsx"
    /// CopyToOutputDirectory="PreserveNewest" /&gt; al .csproj (fuera del alcance de esta tarea).
    /// </summary>
    private static byte[] LoadTemplateBytes()
    {
        const string fileName = "BIT_Matriz_Template_V1_2.xlsx";
        var baseDir = AppContext.BaseDirectory;
        var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? baseDir;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Features", "Waf", "Templates", fileName),
            Path.Combine(baseDir, "Templates", fileName),
            Path.Combine(baseDir, fileName),
            Path.Combine(asmDir, "Features", "Waf", "Templates", fileName),
            Path.Combine(asmDir, "Templates", fileName),
            Path.Combine(asmDir, fileName),
        };
        foreach (var path in candidates)
            if (File.Exists(path))
                return File.ReadAllBytes(path);

        throw new FileNotFoundException(
            "No se encontró la plantilla WAF BIT_Matriz_Template_V1_2.xlsx. " +
            "Verifica que Features/Waf/Templates/BIT_Matriz_Template_V1_2.xlsx se copie al output.");
    }

    // ---------- Llenado de fila (port de _fill_row) ----------

    private static void FillRow(IXLWorksheet sheet, int row, WafExportRow rec)
    {
        var completionPct = rec.CompletionPct ?? 0;
        SetCell(sheet, row, 1, SafeExcelText(rec.ReviewScopeEs));
        SetCell(sheet, row, 2, FormatDate(rec.LastSeenAt) ?? FormatDate(DateTime.Today));
        SetCell(sheet, row, 3, $"{completionPct}%");
        SetCell(sheet, row, 4, SafeExcelText(ResourcesText(rec)));
        SetCell(sheet, row, 5, SafeExcelText(rec.BenefitEs));
        SetCell(sheet, row, 6, SafeExcelText(ActionsText(rec)));
        SetCell(sheet, row, 7, SafeExcelText(ImpactText(rec)));
        SetCell(sheet, row, 8, SafeExcelText(PriorityText(rec)));
        SetCell(sheet, row, 9, FormatDate(rec.RemediationStartDate));
        SetCell(sheet, row, 10, SafeExcelText(rec.ProjectedBitEffort));
        SetCell(sheet, row, 11, StatusText(completionPct));
        SetCell(sheet, row, 12, SafeExcelText(rec.ExecutionLog));

        // Altura dinámica (port exacto). Se miran las columnas 4,5,6,12.
        var heightCols = new[] { 4, 5, 6, 12 };
        var longestText = 0;
        var lineCount = 1;
        foreach (var col in heightCols)
        {
            var text = CellString(sheet, row, col);
            if (text.Length > longestText) longestText = text.Length;
            var lines = CountNewlines(text) + 1;
            if (lines > lineCount) lineCount = lines;
        }
        var wrappedLines = Math.Max(lineCount, Math.Min(6, (longestText / 48) + 1));
        var current = sheet.Row(row).Height;
        if (current <= 0) current = 15;
        sheet.Row(row).Height = Math.Max(current, wrappedLines * 15);
    }

    private static void SetCell(IXLWorksheet sheet, int row, int col, string? value)
    {
        var cell = sheet.Cell(row, col);
        if (value is null) { cell.Clear(XLClearOptions.Contents); return; }
        // SetValue mantiene el string literal (no auto-detecta tipos/fórmulas: el '-prefijo del
        // SafeExcelText ya neutralizó fórmulas, igual que en openpyxl).
        cell.SetValue(value);
    }

    private static string CellString(IXLWorksheet sheet, int row, int col)
    {
        var cell = sheet.Cell(row, col);
        return cell.IsEmpty() ? "" : cell.GetString();
    }

    private static int CountNewlines(string s)
    {
        var count = 0;
        foreach (var ch in s) if (ch == '\n') count++;
        return count;
    }

    // ---------- Helpers de conversión (port 1:1) ----------

    /// <summary>_safe_excel_text: escapa fórmulas (=,+,-,@,\t,\r) prefijando '. Solo aplica a strings.</summary>
    private static string? SafeExcelText(string? value)
    {
        if (value is null) return null;
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            return "'" + value;
        return value;
    }

    /// <summary>_status_text.</summary>
    private static string StatusText(int completionPct) =>
        completionPct >= 100 ? "Completado" : completionPct > 0 ? "En progreso" : "Pendiente";

    /// <summary>_impact_number: int 1-3 o mapeo textual (casefold). null si no clasifica.</summary>
    private static int? ImpactNumber(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case int n:
                return n is 1 or 2 or 3 ? n : null;
        }
        var text = value.ToString();
        if (string.IsNullOrEmpty(text)) return null;
        // try int(value) primero (paridad con int(value) del Python sobre strings numéricas).
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed is 1 or 2 or 3 ? parsed : null;
        return text.Trim().ToLowerInvariant() switch
        {
            "high" or "alto" or "alta" => 1,
            "medium" or "medio" or "media" => 2,
            "low" or "bajo" or "baja" => 3,
            _ => null,
        };
    }

    private static string ImpactLabel(int? number) => number switch
    {
        1 => "1 - ALTA",
        2 => "2 - MEDIA",
        3 => "3 - BAJA",
        _ => "",
    };

    /// <summary>_impact_text: impact_number con fallback a business_impact.</summary>
    private static string ImpactText(WafExportRow rec)
    {
        var number = ImpactNumber(rec.ImpactNumber);
        number ??= ImpactNumber(rec.BusinessImpact);
        return ImpactLabel(number);
    }

    /// <summary>_priority_text: priority_override (int 1-3 o texto) tiene prioridad sobre impacto.</summary>
    private static string PriorityText(WafExportRow rec)
    {
        var overrideValue = rec.PriorityOverride; // int? en el record (no llega texto)
        var number = ImpactNumber(overrideValue);
        if (number is not null) return ImpactLabel(number);
        // override not in (None, ""): un int? distinto de null ya habría entrado arriba; aquí solo
        // queda null → se cae al impacto (paridad: un override no nulo no-1/2/3 devolvería str(override),
        // pero el modelo de datos restringe priority_override a 1-3/null como en BD).
        if (overrideValue is not null) return overrideValue.Value.ToString(CultureInfo.InvariantCulture);
        var impact = ImpactNumber(rec.ImpactNumber);
        impact ??= ImpactNumber(rec.BusinessImpact);
        return ImpactLabel(impact);
    }

    /// <summary>_format_date: date/datetime → "yyyy-MM-dd"; null si vacío.</summary>
    private static string? FormatDate(DateTime? value) =>
        value is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    /// <summary>_resources_text: lista ≤10 unida por \n, o "N recursos" si count &gt; 10.</summary>
    private static string ResourcesText(WafExportRow rec)
    {
        var resources = (rec.Resources ?? [])
            .Select(v => (v ?? "").Trim())
            .Where(v => v.Length > 0)
            .ToList();
        var count = rec.ResourceCount ?? resources.Count;
        if (count <= 0) return "";
        if (count <= MaxResourcesListed)
            return string.Join("\n", resources.Take(MaxResourcesListed));
        return $"{count} recursos";
    }

    /// <summary>_actions_text: "Cliente: ...\n\nBIT: ...".</summary>
    private static string ActionsText(WafExportRow rec)
    {
        var values = new List<string>();
        if (!string.IsNullOrEmpty(rec.ClientActionEs)) values.Add($"Cliente: {rec.ClientActionEs}");
        if (!string.IsNullOrEmpty(rec.BitActionEs)) values.Add($"BIT: {rec.BitActionEs}");
        return string.Join("\n\n", values);
    }

    // _sort_key: (priority_override or 99, impact_number or 99, -(resource_count or 0))
    private static (int, int, int) SortKey(WafExportRow rec) =>
        (rec.PriorityOverride is { } po && po != 0 ? po : 99,
         rec.ImpactNumber is { } im && im != 0 ? im : 99,
         -(rec.ResourceCount ?? 0));

    private sealed class SortKeyComparer : IComparer<(int, int, int)>
    {
        public static readonly SortKeyComparer Instance = new();
        public int Compare((int, int, int) a, (int, int, int) b)
        {
            var c = a.Item1.CompareTo(b.Item1);
            if (c != 0) return c;
            c = a.Item2.CompareTo(b.Item2);
            if (c != 0) return c;
            return a.Item3.CompareTo(b.Item3);
        }
    }

    // ---------- Estilos de fila (port de _copy_row_style / _clear_row) ----------

    private static void CopyRowStyle(IXLWorksheet sheet, int sourceRow, int targetRow, int maxColumn)
    {
        for (var column = 1; column <= maxColumn; column++)
            sheet.Cell(targetRow, column).Style = sheet.Cell(sourceRow, column).Style;
        var sourceHeight = sheet.Row(sourceRow).Height;
        if (sourceHeight > 0) sheet.Row(targetRow).Height = sourceHeight;
    }

    private static void ClearRow(IXLWorksheet sheet, int row, int maxColumn)
    {
        for (var column = 1; column <= maxColumn; column++)
            sheet.Cell(row, column).Clear(XLClearOptions.Contents);
    }
}
