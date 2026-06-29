using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Port fiel 1:1 de app/reports/word_export.py (python-docx) a DocumentFormat.OpenXml.
///
/// Equivalencias python-docx → OpenXml:
///   Document            → WordprocessingDocument (abierto sobre una copia en memoria del template)
///   doc.element.body    → MainDocumentPart.Document.Body
///   Paragraph / Table   → DocumentFormat.OpenXml.Wordprocessing.Paragraph / Table
///   paragraph.runs      → Paragraph.Elements&lt;Run&gt;()
///   run.text            → texto concatenado de los &lt;w:t&gt; del Run
///   block.style.name    → se resuelve por styleId del párrafo (Ttulo1→"Heading 1", etc.)
///   deepcopy(el)        → el.CloneNode(true)
///   el.addprevious/addnext/getparent().remove → InsertBeforeSelf / InsertAfterSelf / Remove
///   qn("w:w")           → atributos w:w del namespace wordprocessingml
///
/// RIESGOS / diferencias documentadas frente a python-docx (ver final del archivo):
///   - Resolución de estilos: python-docx normaliza "heading 1"→"Heading 1" vía su BabelFish; aquí
///     se mapea por styleId real de la plantilla (Ttulo1/2/3) + fallback por nombre del estilo.
///   - Clonado XML preserva formato igual que deepcopy; el resultado estético debe ser idéntico.
///   - La plantilla debe copiarse al output (ver LoadTemplateBytes / nota de .csproj).
/// </summary>
public sealed class ReportWordExporter : IReportWordExporter
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string TemplateFileName = "PLANTILLA-INF-CDC-BIT-v1.docx";

    private static readonly string[] MonthNames =
    {
        "", "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio",
        "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre",
    };

    // _HEADING_LEVEL = {"Heading 1": 1, "Heading 2": 2, "Heading 3": 3}
    private static readonly Dictionary<string, int> HeadingLevelByName = new(StringComparer.Ordinal)
    {
        ["Heading 1"] = 1,
        ["Heading 2"] = 2,
        ["Heading 3"] = 3,
    };

    // ---------- API pública ----------

    /// <summary>build_word_report: genera el .docx del informe a partir del JSON almacenado.</summary>
    public byte[] BuildWordReport(JsonElement report)
    {
        var templateBytes = LoadTemplateBytes();
        using var ms = new MemoryStream();
        ms.Write(templateBytes, 0, templateBytes.Length);
        ms.Position = 0;

        using (var doc = WordprocessingDocument.Open(ms, isEditable: true))
        {
            var ctx = new Doc(doc);

            FillCover(ctx, report);
            RemoveInstructionsPage(ctx);

            FillPermissions(ctx, report);
            FillServers(ctx, report);
            FillTemplates(ctx, report);
            FillResourceStatus(ctx, report);
            FillAppServices(ctx, report);
            FillConnectivity(ctx, report);
            FillBackups(ctx, report);
            FillPerformance(ctx, report);
            FillSql(ctx, report);
            FillAlerts(ctx, report);
            FillAvailability(ctx, report);
            FillConclusions(ctx, report);
            RemoveUnusedOptionalSections(ctx);

            RemoveGuides(ctx);
            RemoveImagePlaceholders(ctx, new[]
            {
                "Insertar imagen", "Insertar gráficas", "Insertar graficas",
                "Insertar métricas", "Insertar metricas", "Insertar gráfica",
            });
            StripOpcionalSuffix(ctx);

            ctx.Document.Save();
        }

        return ms.ToArray();
    }

    /// <summary>word_filename: [CLIENTE]-INF-CDC-[MES]-[AAAA]-v1.docx.</summary>
    public string WordFilename(JsonElement report)
    {
        var client = GetStr(GetProp(GetProp(report, "client"), "name")) ?? "CLIENTE";
        var period = GetProp(report, "period");
        var month = GetInt(GetProp(period, "month")) ?? 0;
        var monthName = month > 0 && month <= 12 ? MonthNames[month].ToUpperInvariant() : "MES";
        var year = GetRaw(GetProp(period, "year")) ?? "AAAA";

        // unicodedata NFKD + strip a ASCII alfanumérico en mayúsculas.
        var normalized = new string(client.Normalize(NormalizationForm.FormKD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());
        normalized = Encoding.ASCII.GetString(Encoding.Convert(
            Encoding.UTF8, Encoding.GetEncoding(
                Encoding.ASCII.CodePage,
                new EncoderReplacementFallback(""),
                new DecoderReplacementFallback("")),
            Encoding.UTF8.GetBytes(normalized)));
        normalized = Regex.Replace(normalized, "[^A-Za-z0-9]+", "").ToUpperInvariant();
        if (normalized.Length == 0) normalized = "CLIENTE";
        return $"{normalized}-INF-CDC-{monthName}-{year}-v1.docx";
    }

    // ---------- Contexto del documento ----------

    private sealed class Doc
    {
        public WordprocessingDocument Pkg { get; }
        public MainDocumentPart MainPart { get; }
        public Document Document { get; }
        public Body Body { get; }

        public Doc(WordprocessingDocument pkg)
        {
            Pkg = pkg;
            MainPart = pkg.MainDocumentPart
                ?? throw new InvalidOperationException("La plantilla no tiene MainDocumentPart.");
            Document = MainPart.Document
                ?? throw new InvalidOperationException("La plantilla no tiene Document.");
            Body = Document.Body
                ?? throw new InvalidOperationException("La plantilla no tiene Body.");
        }
    }

    // ---------- Utilidades de documento (port de las funciones _xxx) ----------

    /// <summary>_body_blocks: párrafos y tablas del cuerpo, en orden.</summary>
    private static List<OpenXmlElement> BodyBlocks(Doc doc) =>
        doc.Body.ChildElements.Where(e => e is Paragraph or Table).ToList();

    /// <summary>style.name del párrafo: resuelve styleId → nombre del estilo.</summary>
    private static string? ParagraphStyleName(Doc doc, Paragraph p)
    {
        var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (styleId is null) return "Normal"; // python-docx: párrafo sin pStyle hereda "Normal"
        var style = doc.MainPart.StyleDefinitionsPart?.Styles?
            .Elements<Style>()
            .FirstOrDefault(s => s.StyleId?.Value == styleId);
        var name = style?.StyleName?.Val?.Value;
        if (string.IsNullOrEmpty(name)) name = styleId;
        return NormalizeBuiltinName(name!);
    }

    /// <summary>
    /// Replica la normalización de nombres de python-docx (BabelFish) para los estilos que importan:
    /// "heading N" → "Heading N", "List Paragraph", "Normal", "Title". Para el resto deja el nombre tal cual.
    /// </summary>
    private static string NormalizeBuiltinName(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        var m = Regex.Match(lower, @"^heading\s*(\d+)$");
        if (m.Success) return $"Heading {m.Groups[1].Value}";
        return name;
    }

    /// <summary>_heading_level: nivel 1/2/3 si el bloque es un heading, null si no.</summary>
    private static int? HeadingLevel(Doc doc, OpenXmlElement block)
    {
        if (block is Paragraph p)
        {
            var name = ParagraphStyleName(doc, p);
            if (name is not null && HeadingLevelByName.TryGetValue(name, out var lvl)) return lvl;
        }
        return null;
    }

    /// <summary>_delete: quita el bloque de su padre.</summary>
    private static void DeleteBlock(OpenXmlElement element) => element.Remove();

    /// <summary>Texto del bloque (paragraph.text / table sin uso directo): concatena los &lt;w:t&gt;.</summary>
    private static string ParagraphText(Paragraph p) =>
        string.Concat(p.Descendants<Text>().Select(t => t.Text));

    private static string CellText(TableCell c) =>
        string.Join("\n", c.Elements<Paragraph>().Select(ParagraphText));

    private static List<Run> ParagraphRuns(Paragraph p) => p.Elements<Run>().ToList();

    /// <summary>_set_paragraph_text: reemplaza el texto conservando el formato del primer run.</summary>
    private static void SetParagraphText(Paragraph paragraph, string text)
    {
        var runs = ParagraphRuns(paragraph);
        if (runs.Count == 0)
        {
            // paragraph.add_run(text)
            var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            paragraph.AppendChild(run);
            return;
        }
        // runs[0].text = text  → vacía todos los <w:t> del primer run y deja uno con el texto.
        SetRunText(runs[0], text);
        // runs[1:].text = ""
        for (var i = 1; i < runs.Count; i++) SetRunText(runs[i], "");
    }

    /// <summary>Equivalente a run.text = value: deja un único &lt;w:t&gt; con value (o ninguno si value vacío).</summary>
    private static void SetRunText(Run run, string value)
    {
        foreach (var t in run.Elements<Text>().ToList()) t.Remove();
        // Conserva otros hijos (rPr, breaks) tal cual; python-docx run.text reescribe solo w:t/w:tab/w:br
        // textuales, pero en esta plantilla los runs de placeholders son texto plano.
        var text = new Text(value) { Space = SpaceProcessingModeValues.Preserve };
        run.AppendChild(text);
    }

    /// <summary>_replace_in_paragraph: aplica mapping al texto del párrafo si cambia algo.</summary>
    private static void ReplaceInParagraph(Paragraph paragraph, IReadOnlyList<KeyValuePair<string, string>> mapping)
    {
        var text = ParagraphText(paragraph);
        var replaced = text;
        foreach (var kv in mapping) replaced = replaced.Replace(kv.Key, kv.Value);
        if (replaced != text) SetParagraphText(paragraph, replaced);
    }

    /// <summary>_replace_everywhere: aplica mapping a todos los párrafos y celdas del documento.</summary>
    private static void ReplaceEverywhere(Doc doc, IReadOnlyList<KeyValuePair<string, string>> mapping)
    {
        foreach (var p in doc.Body.Descendants<Paragraph>()) ReplaceInParagraph(p, mapping);
        // doc.tables → doc.paragraphs ya incluye las celdas (Descendants), pero python recorre tablas aparte;
        // Descendants<Paragraph> ya cubre ambos casos sin duplicar (un párrafo no se procesa dos veces).
    }

    private static string StripOpcional(string text) =>
        Regex.Replace(text.Trim(), @"\s*\(opcional\)\s*$", "", RegexOptions.IgnoreCase);

    /// <summary>_find_heading: primer heading cuyo texto (sin "(opcional)") empieza por title.</summary>
    private static Paragraph? FindHeading(Doc doc, string title)
    {
        var normalized = title.Trim().ToLowerInvariant();
        foreach (var block in BodyBlocks(doc))
        {
            if (HeadingLevel(doc, block) is not null && block is Paragraph p)
            {
                var heading = StripOpcional(ParagraphText(p));
                if (heading.Trim().ToLowerInvariant().StartsWith(normalized, StringComparison.Ordinal))
                    return p;
            }
        }
        return null;
    }

    /// <summary>_remove_section: elimina el heading y su contenido hasta el siguiente heading de nivel ≤.</summary>
    private static void RemoveSection(Doc doc, string title)
    {
        var heading = FindHeading(doc, title);
        if (heading is null) return;
        var level = HeadingLevel(doc, heading)!.Value;
        var removing = false;
        foreach (var block in BodyBlocks(doc))
        {
            if (ReferenceEquals(block, heading))
            {
                removing = true;
                DeleteBlock(block);
                continue;
            }
            if (removing)
            {
                var blockLevel = HeadingLevel(doc, block);
                if (blockLevel is not null && blockLevel.Value <= level) break;
                DeleteBlock(block);
            }
        }
    }

    /// <summary>_table_after_heading: primera tabla tras el heading (null si aparece otro heading antes).</summary>
    private static Table? TableAfterHeading(Doc doc, string title)
    {
        var heading = FindHeading(doc, title);
        if (heading is null) return null;
        var found = false;
        foreach (var block in BodyBlocks(doc))
        {
            if (ReferenceEquals(block, heading)) { found = true; continue; }
            if (found)
            {
                if (block is Table t) return t;
                if (HeadingLevel(doc, block) is not null) return null;
            }
        }
        return null;
    }

    private static List<TableRow> TableRows(Table table) => table.Elements<TableRow>().ToList();
    private static List<TableCell> RowCells(TableRow row) => row.Elements<TableCell>().ToList();

    /// <summary>_fill_table: reemplaza la fila placeholder (la última) por las filas de datos clonando su XML.</summary>
    private static void FillTable(Table table, IEnumerable<object?[]> rows)
    {
        var allRows = TableRows(table);
        var placeholder = allRows[^1];
        foreach (var values in rows)
        {
            var newTr = (TableRow)placeholder.CloneNode(true);
            placeholder.InsertBeforeSelf(newTr);
            // new_row = table.rows[-2]  → la fila recién insertada (justo antes del placeholder).
            var newRow = newTr;
            var cells = RowCells(newRow);
            for (var i = 0; i < cells.Count && i < values.Length; i++)
            {
                var cell = cells[i];
                var paragraphs = cell.Elements<Paragraph>().ToList();
                var paragraph = paragraphs[0];
                // Vacía todos los runs del primer párrafo.
                foreach (var run in paragraph.Elements<Run>().ToList()) run.Remove();
                // Borra los párrafos extra de la celda.
                for (var j = 1; j < paragraphs.Count; j++) paragraphs[j].Remove();
                // paragraph.add_run(_fmt(value))
                paragraph.AppendChild(new Run(new Text(Fmt(values[i])) { Space = SpaceProcessingModeValues.Preserve }));
            }
        }
        // _delete_row(table, placeholder)
        placeholder.Remove();
    }

    /// <summary>_fmt: None/"" → "-"; float → 1 decimal; else str(value).</summary>
    private static string Fmt(object? value)
    {
        if (value is null) return "-";
        if (value is string s) return s.Length == 0 ? "-" : s;
        if (value is double d) return d.ToString("0.0", CultureInfo.InvariantCulture);
        if (value is float f) return ((double)f).ToString("0.0", CultureInfo.InvariantCulture);
        var asString = value.ToString();
        return string.IsNullOrEmpty(asString) ? "-" : asString;
    }

    /// <summary>_pct: None → "-"; else "x.x %".</summary>
    private static string Pct(double? value) =>
        value is null ? "-" : $"{value.Value.ToString("0.0", CultureInfo.InvariantCulture)} %";

    /// <summary>_avg_max: 'prom / max %' para columnas combinadas.</summary>
    private static string AvgMax(double? avg, double? max)
    {
        if (avg is null && max is null) return "-";
        string F(double? v) => v is null ? "-" : v.Value.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{F(avg)} / {F(max)} %";
    }

    /// <summary>_uptime_word: '% (horas)' del mes; 'n/d' si no hay uptime_pct.</summary>
    private static string UptimeWord(JsonElement vm)
    {
        var pct = GetDouble(GetProp(vm, "uptime_pct"));
        if (pct is null) return "n/d";
        var hours = GetDouble(GetProp(vm, "running_hours"));
        if (hours is null) return $"{(int)Math.Round(pct.Value, MidpointRounding.ToEven)} %";
        return $"{(int)Math.Round(pct.Value, MidpointRounding.ToEven)} % ({(int)Math.Round(hours.Value, MidpointRounding.ToEven)} h)";
    }

    /// <summary>_append_columns: agrega columnas clonando la última y reescala anchos.</summary>
    private static void AppendColumns(Table table, int count)
    {
        var grid = table.GetFirstChild<TableGrid>();
        if (grid is null) return;

        int ColW(GridColumn c) => ParseInt(c.Width?.Value) ?? 0;
        var originalTotal = grid.Elements<GridColumn>().Sum(ColW);

        for (var n = 0; n < count; n++)
        {
            var cols = grid.Elements<GridColumn>().ToList();
            var newCol = (GridColumn)cols[^1].CloneNode(true);
            cols[^1].InsertAfterSelf(newCol);
            foreach (var row in TableRows(table))
            {
                var cells = RowCells(row);
                var lastTc = cells[^1];
                var newTc = (TableCell)lastTc.CloneNode(true);
                lastTc.InsertAfterSelf(newTc);
                // new_cell = row.cells[-1]  → la celda recién insertada.
                var newCell = newTc;
                var paragraphs = newCell.Elements<Paragraph>().ToList();
                for (var i = 1; i < paragraphs.Count; i++) paragraphs[i].Remove();
                foreach (var run in paragraphs[0].Elements<Run>())
                    SetRunText(run, "");
            }
        }

        var allCols = grid.Elements<GridColumn>().ToList();
        var total = allCols.Sum(ColW);
        if (total == 0 || originalTotal == 0) return;
        var factor = (double)originalTotal / total;
        foreach (var col in allCols)
        {
            var width = ParseInt(col.Width?.Value);
            if (width is not null) col.Width = ((int)(width.Value * factor)).ToString(CultureInfo.InvariantCulture);
        }
        foreach (var row in TableRows(table))
        {
            foreach (var cell in RowCells(row))
            {
                var tcW = cell.TableCellProperties?.TableCellWidth;
                if (tcW is not null && tcW.Type?.Value == TableWidthUnitValues.Dxa)
                {
                    var width = ParseInt(tcW.Width?.Value);
                    if (width is not null) tcW.Width = ((int)(width.Value * factor)).ToString(CultureInfo.InvariantCulture);
                }
            }
        }
    }

    /// <summary>_strip_opcional_suffix: quita " (Opcional)" de los headings que lo tengan.</summary>
    private static void StripOpcionalSuffix(Doc doc)
    {
        foreach (var block in BodyBlocks(doc))
        {
            if (HeadingLevel(doc, block) is not null && block is Paragraph p)
            {
                var text = ParagraphText(p);
                if (Regex.IsMatch(text, @"\(opcional\)\s*$", RegexOptions.IgnoreCase))
                    SetParagraphText(p, Regex.Replace(text, @"\s*\(opcional\)\s*$", "", RegexOptions.IgnoreCase));
            }
        }
    }

    /// <summary>_remove_guides: elimina recuadros 'Guía:' y notas internas.</summary>
    private static void RemoveGuides(Doc doc)
    {
        string[] internalPrefixes = { "guía:", "guia:", "convención de nombre del archivo", "convencion de nombre del archivo" };
        foreach (var block in BodyBlocks(doc))
        {
            if (block is Paragraph p)
            {
                var text = ParagraphText(p).Trim().ToLowerInvariant();
                if (internalPrefixes.Any(pre => text.StartsWith(pre, StringComparison.Ordinal)))
                    DeleteBlock(p);
            }
        }
    }

    /// <summary>_remove_instructions_page: elimina la página de instrucciones hasta CONTENIDO (exclusivo).</summary>
    private static void RemoveInstructionsPage(Doc doc)
    {
        var removing = false;
        foreach (var block in BodyBlocks(doc))
        {
            if (block is Paragraph p)
            {
                var text = ParagraphText(p).Trim();
                if (text.StartsWith("Instrucciones de uso de la plantilla", StringComparison.Ordinal))
                    removing = true;
                else if (removing && text.ToUpperInvariant().StartsWith("CONTENIDO", StringComparison.Ordinal))
                    return;
            }
            if (removing) DeleteBlock(block);
        }
    }

    /// <summary>_remove_image_placeholders: elimina párrafos '[...Insertar...]'.</summary>
    private static void RemoveImagePlaceholders(Doc doc, IReadOnlyList<string> fragments)
    {
        foreach (var block in BodyBlocks(doc))
        {
            if (block is Paragraph p)
            {
                var text = ParagraphText(p).Trim();
                if (text.StartsWith("[", StringComparison.Ordinal) && fragments.Any(text.Contains))
                    DeleteBlock(p);
            }
        }
    }

    /// <summary>_fill_list_placeholders: reemplaza [Conclusión N]/[Recomendación N] por la lista real.</summary>
    private static void FillListPlaceholders(Doc doc, string prefix, IReadOnlyList<string> items)
    {
        var placeholders = BodyBlocks(doc)
            .OfType<Paragraph>()
            .Where(p => ParagraphText(p).Trim().StartsWith($"[{prefix}", StringComparison.Ordinal))
            .ToList();
        if (placeholders.Count == 0 || items.Count == 0) return; // sin narrativa: el analista completa

        var pairCount = Math.Min(placeholders.Count, items.Count);
        for (var i = 0; i < pairCount; i++) SetParagraphText(placeholders[i], items[i]);

        // last = placeholders[min(len(items), len(placeholders)) - 1]
        var last = placeholders[Math.Min(items.Count, placeholders.Count) - 1];
        // items[len(placeholders):]  → clonar el último párrafo por cada item sobrante.
        for (var i = placeholders.Count; i < items.Count; i++)
        {
            var clone = (Paragraph)last.CloneNode(true);
            last.InsertAfterSelf(clone);
            last = clone;
            SetParagraphText(last, items[i]);
        }
        // placeholders[len(items):]  → borrar placeholders sobrantes.
        for (var i = items.Count; i < placeholders.Count; i++) DeleteBlock(placeholders[i]);
    }

    // ---------- Relleno de secciones (port de las funciones _fill_xxx) ----------

    private static void FillCover(Doc doc, JsonElement report)
    {
        var period = GetProp(report, "period");
        var clientName = GetStr(GetProp(GetProp(report, "client"), "name")) ?? "Cliente";
        var year = GetRaw(GetProp(period, "year")) ?? "";
        var month = GetInt(GetProp(period, "month")) ?? 0;
        var end = GetStr(GetProp(period, "end")) ?? "";
        var lastDay = end.Length >= 10 ? end.Substring(8, 2) : "";
        var monthName = month > 0 && month <= 12 ? MonthNames[month] : "";
        var today = DateTime.UtcNow.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

        var mapping = new List<KeyValuePair<string, string>>
        {
            new("[NOMBRE DEL CLIENTE]", clientName),
            new("[DD]", lastDay.Length > 0 ? lastDay : "[DD]"),
            new("[MES]", monthName.Length > 0 ? monthName.ToLowerInvariant() : "[MES]"),
            new("[AAAA]", year.Length > 0 ? year : "[AAAA]"),
        };
        var generatedBy = GetStr(GetProp(report, "generated_by"));
        if (!string.IsNullOrEmpty(generatedBy))
            mapping.Add(new("[Nombre del analista]", generatedBy));
        ReplaceEverywhere(doc, mapping);

        // La fecha de elaboración solo aplica a la tabla "Elaborado por".
        foreach (var table in doc.Body.Descendants<Table>())
        {
            var rows = TableRows(table);
            if (rows.Count == 0) continue;
            var firstCell = RowCells(rows[0]).FirstOrDefault();
            if (firstCell is not null && CellText(firstCell).Contains("Elaborado por"))
            {
                var dateMap = new[] { new KeyValuePair<string, string>("[DD-MM-AAAA]", today) };
                foreach (var row in rows)
                    foreach (var cell in RowCells(row))
                        foreach (var p in cell.Elements<Paragraph>())
                            ReplaceInParagraph(p, dateMap);
                break;
            }
        }
    }

    private static void FillPermissions(Doc doc, JsonElement report)
    {
        var permisos = GetProp(report, "permisos");
        var porSub = AsArray(GetProp(permisos, "por_suscripcion"));
        var resumen = GetProp(permisos, "resumen");
        if (porSub.Count == 0) return; // sin datos: placeholders manuales
        var table = TableAfterHeading(doc, "Permisos y roles");
        if (table is null) return;

        var headerRow = TableRows(table)[0];
        var headers = new[] { "Suscripcion", "Total", "Elevados", "% Elev.", "Owners",
            "Contributors", "UAA", "Usuarios", "SP" };
        var headerCells = RowCells(headerRow);
        for (var i = 0; i < headerCells.Count && i < headers.Length; i++)
            SetParagraphText(headerCells[i].Elements<Paragraph>().First(), headers[i]);

        var rows = new List<object?[]>();
        foreach (var s in porSub)
        {
            var total = GetInt(GetProp(s, "total"));
            var elevados = GetInt(GetProp(s, "elevados"));
            // Python: round(elevados/total*100, 1) (float → str con ".0") si total; si no, int 0 (sin ".0").
            string pctElev = total is { } t && t != 0
                ? PyRoundStr(Math.Round((elevados ?? 0) / (double)t * 100, 1, MidpointRounding.ToEven))
                : "0";
            rows.Add(new object?[]
            {
                GetRaw(GetProp(s, "suscripcion")), total, elevados,
                $"{pctElev} %",
                GetRaw(GetProp(s, "owners")), GetRaw(GetProp(s, "contributors")),
                GetRaw(GetProp(s, "uaa")), GetRaw(GetProp(s, "usuarios")),
                GetRaw(GetProp(s, "service_principals")),
            });
        }
        FillTable(table, rows);

        // Línea de resumen después del heading.
        var heading = FindHeading(doc, "Permisos y roles");
        if (heading is not null && resumen.ValueKind == JsonValueKind.Object)
        {
            var total = GetInt(GetProp(resumen, "total")) ?? 0;
            var elevados = GetInt(GetProp(resumen, "elevados")) ?? 0;
            var pct = GetRaw(GetProp(resumen, "pct_elevados")) ?? "0";
            var owners = GetInt(GetProp(resumen, "owners")) ?? 0;
            var rootT = GetInt(GetProp(resumen, "root_tenant")) ?? 0;
            var unicas = GetInt(GetProp(resumen, "cuentas_unicas")) ?? 0;
            var summaryText =
                $"Total: {total} asignaciones | Elevados: {elevados} ({pct}%) | " +
                $"Owners: {owners} | Root tenant: {rootT} | Cuentas unicas: {unicas}";
            var clone = (Paragraph)heading.CloneNode(true);
            heading.InsertAfterSelf(clone);
            SetParagraphStyle(doc, clone, "Normal");
            SetParagraphText(clone, summaryText);
        }
    }

    private static void FillServers(Doc doc, JsonElement report)
    {
        var inventory = AsArray(GetProp(report, "inventario"));
        var table = TableAfterHeading(doc, "Listado de servidores");
        if (table is not null && inventory.Count > 0)
        {
            AppendColumns(table, 1);
            var lastHeaderCell = RowCells(TableRows(table)[0])[^1];
            SetParagraphText(lastHeaderCell.Elements<Paragraph>().First(), "% Uptime (mes)");
            FillTable(table, inventory.Select(vm => new object?[]
            {
                GetRaw(GetProp(vm, "name")), GetRaw(GetProp(vm, "ip")), GetRaw(GetProp(vm, "subscription")),
                GetRaw(GetProp(vm, "location")), GetRaw(GetProp(vm, "status")), GetRaw(GetProp(vm, "os")),
                GetRaw(GetProp(vm, "size")), GetRaw(GetProp(vm, "disks")), UptimeWord(vm),
            }));
        }

        var detail = TableAfterHeading(doc, "Inventario / detalle de servidores");
        if (detail is not null && inventory.Count > 0)
        {
            FillTable(detail, inventory.Select(vm => new object?[]
            {
                GetRaw(GetProp(vm, "name")), GetRaw(GetProp(vm, "vcpu")), GetRaw(GetProp(vm, "ram_gb")), "-",
                GetRaw(GetProp(vm, "disks")), "-", "-",
            }));
        }
        else if (inventory.Count == 0)
        {
            RemoveSection(doc, "Inventario / detalle de servidores");
        }
    }

    private static void FillTemplates(Doc doc, JsonElement report)
    {
        var templates = AsArray(GetProp(report, "plantillas"));
        if (templates.Count == 0)
        {
            RemoveSection(doc, "Plantillas y sistema operativo");
            return;
        }
        var table = TableAfterHeading(doc, "Plantillas y sistema operativo");
        if (table is not null)
            FillTable(table, templates.Select(tpl => new object?[]
            {
                GetRaw(GetProp(tpl, "sku")), GetRaw(GetProp(tpl, "vcpu")), GetRaw(GetProp(tpl, "ram_gb")),
                GetRaw(GetProp(tpl, "vms")), GetRaw(GetProp(tpl, "os")),
            }));
    }

    private static void FillResourceStatus(Doc doc, JsonElement report)
    {
        var status = AsArray(GetProp(report, "estado_recursos"));
        var table = TableAfterHeading(doc, "Estado de los recursos");
        if (table is not null && status.Count > 0)
            FillTable(table, status.Select(row => new object?[]
            {
                GetRaw(GetProp(row, "tipo")), GetRaw(GetProp(row, "total")), GetRaw(GetProp(row, "disponibles")),
                GetRaw(GetProp(row, "con_alerta")), GetRaw(GetProp(row, "obs")),
            }));
    }

    private static void FillAppServices(Doc doc, JsonElement report)
    {
        var plans = AsArray(GetProp(report, "app_services"));
        if (plans.Count == 0)
        {
            RemoveSection(doc, "App Services Plan");
            return;
        }
        var table = TableAfterHeading(doc, "App Services Plan");
        if (table is null) return;

        var perfPlans = AsArray(GetProp(GetProp(report, "performance"), "app_service_plans"));
        var metrics = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in perfPlans)
        {
            var key = GetStr(GetProp(item, "resource_name"));
            if (key is not null) metrics[key] = item;
        }

        AppendColumns(table, 2);
        var headers = new[] { "Plan", "SKU / Tier", "Instancias", "Apps", "CPU prom %", "RAM prom %", "Estado" };
        var headerCells = RowCells(TableRows(table)[0]);
        for (var i = 0; i < headerCells.Count && i < headers.Length; i++)
            SetParagraphText(headerCells[i].Elements<Paragraph>().First(), headers[i]);

        var rows = new List<object?[]>();
        foreach (var plan in plans)
        {
            var planName = GetStr(GetProp(plan, "plan"));
            metrics.TryGetValue(planName ?? "\0__none__\0", out var metric);
            rows.Add(new object?[]
            {
                GetRaw(GetProp(plan, "plan")), GetRaw(GetProp(plan, "sku")), GetRaw(GetProp(plan, "instancias")),
                GetRaw(GetProp(plan, "apps")), Pct(GetDouble(GetProp(metric, "cpu_avg"))),
                Pct(GetDouble(GetProp(metric, "ram_avg"))), GetRaw(GetProp(plan, "estado")),
            });
        }
        FillTable(table, rows);
    }

    private static void FillConnectivity(Doc doc, JsonElement report)
    {
        var items = AsArray(GetProp(report, "conectividad"));
        if (items.Count == 0)
        {
            RemoveSection(doc, "Conectividad");
            return;
        }
        var table = TableAfterHeading(doc, "Conectividad");
        if (table is not null)
            FillTable(table, items.Select(item => new object?[]
            {
                GetRaw(GetProp(item, "nombre")), GetRaw(GetProp(item, "proveedor")), GetRaw(GetProp(item, "ancho")),
                GetRaw(GetProp(item, "estado")), GetRaw(GetProp(item, "obs")),
            }));
    }

    private static void FillBackups(Doc doc, JsonElement report)
    {
        var backups = GetProp(report, "backups");
        var vaults = AsArray(GetProp(backups, "vaults"));
        var items = AsArray(GetProp(backups, "items"));
        var jobs = GetProp(backups, "jobs_mes");
        var jobsObj = jobs.ValueKind == JsonValueKind.Object ? jobs.EnumerateObject().ToList() : new List<JsonProperty>();
        if (vaults.Count == 0 && items.Count == 0)
        {
            RemoveSection(doc, "Respaldos (Backup)");
            return;
        }

        var vaultTable = TableAfterHeading(doc, "Recovery Services Vault");
        if (vaultTable is not null && vaults.Count > 0)
            FillTable(vaultTable, vaults.Select(vault => new object?[]
            {
                GetRaw(GetProp(vault, "vault")), GetRaw(GetProp(vault, "region")), GetRaw(GetProp(vault, "elementos")),
                GetRaw(GetProp(vault, "politicas")), GetRaw(GetProp(vault, "ultimo")),
            }));
        else if (vaults.Count == 0)
            RemoveSection(doc, "Recovery Services Vault");

        var itemsTable = TableAfterHeading(doc, "Detalle de backup de servidores");
        if (itemsTable is not null && items.Count > 0)
            FillTable(itemsTable, items.Select(item => new object?[]
            {
                GetRaw(GetProp(item, "servidor")), GetRaw(GetProp(item, "tipo")),
                GetStr(GetProp(item, "frecuencia")) is { Length: > 0 } f ? f : "Segun politica",
                GetRaw(GetProp(item, "retencion")), GetRaw(GetProp(item, "estado")),
            }));
        else if (items.Count == 0)
            RemoveSection(doc, "Detalle de backup de servidores");

        var jobsTable = TableAfterHeading(doc, "Backup jobs");
        var periodLabel = GetStr(GetProp(GetProp(report, "period"), "label")) ?? "-";
        if (jobsTable is not null && jobsObj.Count > 0)
            FillTable(jobsTable, jobsObj.Select(kv => new object?[]
            {
                $"{GetRaw(kv.Value)} trabajo(s)", periodLabel, "Programado", kv.Name, "-",
            }));
        else if (jobsObj.Count == 0)
            RemoveSection(doc, "Backup jobs");

        // Subsecciones sin datos automatizables.
        RemoveSection(doc, "Solución de site alterno (DR)");
        RemoveSection(doc, "Azure File Share");
    }

    private static void FillPerformance(Doc doc, JsonElement report)
    {
        var vms = AsArray(GetProp(GetProp(report, "performance"), "virtual_machines"));
        var table = TableAfterHeading(doc, "Desempeño (Performance) de CPU y RAM");
        if (table is not null && vms.Count > 0)
            FillTable(table, vms.Select(vm => new object?[]
            {
                GetRaw(GetProp(vm, "resource_name")), Pct(GetDouble(GetProp(vm, "cpu_avg"))),
                Pct(GetDouble(GetProp(vm, "cpu_max"))), Pct(GetDouble(GetProp(vm, "ram_avg"))),
                Pct(GetDouble(GetProp(vm, "ram_max"))), PerfObservation(vm),
            }));

        var topTable = TableAfterHeading(doc, "Performance Top 10");
        if (topTable is not null && vms.Count > 0)
        {
            var top = vms.OrderByDescending(vm => GetDouble(GetProp(vm, "cpu_max")) ?? 0).Take(10).ToList();
            var rows = new List<object?[]>();
            for (var index = 0; index < top.Count; index++)
            {
                var vm = top[index];
                rows.Add(new object?[]
                {
                    index + 1, GetRaw(GetProp(vm, "resource_name")),
                    $"CPU {Pct(GetDouble(GetProp(vm, "cpu_max")))} / RAM {Pct(GetDouble(GetProp(vm, "ram_max")))}",
                    "-", PerfObservation(vm),
                });
            }
            FillTable(topTable, rows);
        }
        else if (vms.Count == 0)
        {
            RemoveSection(doc, "Performance Top 10");
        }

        RemoveSection(doc, "Desempeño por entorno");
        RemoveSection(doc, "Desempeño por suscripción");
        RemoveSection(doc, "Azure Virtual Desktop Insights");
    }

    /// <summary>_perf_observation.</summary>
    private static string PerfObservation(JsonElement vm)
    {
        var notes = new List<string>();
        var ramAvg = GetDouble(GetProp(vm, "ram_avg"));
        var cpuAvg = GetDouble(GetProp(vm, "cpu_avg"));
        if ((ramAvg ?? 0) > 80) notes.Add("Alto uso de RAM");
        if ((cpuAvg ?? 0) > 70) notes.Add("Alto uso de CPU");
        if ((cpuAvg ?? 100) < 5 && (ramAvg ?? 100) < 20) notes.Add("Bajo uso (candidata a optimizacion)");
        return notes.Count > 0 ? string.Join(" / ", notes) : "-";
    }

    private static void FillSql(Doc doc, JsonElement report)
    {
        var instances = AsArray(GetProp(report, "sql_mi"));
        if (instances.Count == 0)
        {
            RemoveSection(doc, "Base de datos y almacenamiento SQL");
            return;
        }
        var table = TableAfterHeading(doc, "Base de datos y almacenamiento SQL");
        if (table is not null)
        {
            SetParagraphText(RowCells(TableRows(table)[0])[3].Elements<Paragraph>().First(), "CPU prom / máx %");
            var rows = new List<object?[]>();
            foreach (var mi in instances)
            {
                var used = GetRaw(GetProp(mi, "storage_used_gb"));
                var total = GetRaw(GetProp(mi, "storage_gb"));
                var usedIsNull = GetProp(mi, "storage_used_gb").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
                var totalIsNullOrZero = total is null || total == "0" || GetProp(mi, "storage_gb").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
                string storage = !usedIsNull ? $"{used} / {total} GB" : (!totalIsNullOrZero ? $"{total} GB" : "-");
                var bases = GetRaw(GetProp(mi, "bases")) ?? "-";
                // Python usa `mi.get('tier') or '-'` (falsy): "" también cae a "-". El KQL hace
                // tostring(sku.tier) → "" cuando falta, así que el `??` (solo null) no basta.
                var tier = GetRaw(GetProp(mi, "tier")) is { Length: > 0 } t ? t : "-";
                var vcores = GetRaw(GetProp(mi, "vcores")) is { Length: > 0 } v ? v : "-";
                rows.Add(new object?[]
                {
                    $"{bases} base(s)", GetRaw(GetProp(mi, "name")),
                    $"{tier} / {vcores} vCore",
                    AvgMax(GetDouble(GetProp(mi, "cpu_avg")), GetDouble(GetProp(mi, "cpu_max"))),
                    storage, GetRaw(GetProp(mi, "estado")),
                });
            }
            FillTable(table, rows);
        }
        RemoveSection(doc, "Análisis de evolución mensual");
        RemoveSection(doc, "Análisis de recursos huérfanos");
    }

    private static void FillAlerts(Doc doc, JsonElement report)
    {
        var alertas = GetProp(report, "alertas");
        var resumen = GetProp(alertas, "resumen");
        if (resumen.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || IsFalsy(resumen))
        {
            RemoveSection(doc, "Monitoreo y alertas");
            return;
        }

        RemoveSection(doc, "Azure Monitor");
        RemoveSection(doc, "Incidentes");

        var table = TableAfterHeading(doc, "Alertas del período");
        if (table is not null)
        {
            var headers = new[] { "Tipo de alerta", "Severidad", "Disparos", "Críticas" };
            var headerCells = RowCells(TableRows(table)[0]);
            for (var i = 0; i < headerCells.Count && i < headers.Length; i++)
                SetParagraphText(headerCells[i].Elements<Paragraph>().First(), headers[i]);
            var porTipo = AsArray(GetProp(alertas, "por_tipo"));
            if (porTipo.Count > 0)
                FillTable(table, porTipo.Select(tipo => new object?[]
                {
                    GetRaw(GetProp(tipo, "tipo")), GetRaw(GetProp(tipo, "severidad")),
                    GetRaw(GetProp(tipo, "disparos")), GetRaw(GetProp(tipo, "criticas")),
                }));
            else
                FillTable(table, new[] { new object?[] { "Sin disparos en el período", "-", 0, 0 } });
        }

        var heading = FindHeading(doc, "Monitoreo y alertas");
        if (heading is not null)
        {
            var summaryText =
                $"Disparos en el período: {GetInt(GetProp(resumen, "total")) ?? 0} | " +
                $"Críticas (Sev0/Sev1): {GetInt(GetProp(resumen, "criticas")) ?? 0} | " +
                $"Reglas activadas: {GetInt(GetProp(resumen, "reglas_activadas")) ?? 0} | " +
                $"Suscripciones con alertas: {GetInt(GetProp(resumen, "suscripciones")) ?? 0}";
            var nota = GetStr(GetProp(alertas, "nota"));
            if (!string.IsNullOrEmpty(nota)) summaryText += $". {nota}";
            var clone = (Paragraph)heading.CloneNode(true);
            heading.InsertAfterSelf(clone);
            SetParagraphStyle(doc, clone, "Normal");
            SetParagraphText(clone, summaryText);
        }
    }

    private static void FillAvailability(Doc doc, JsonElement report)
    {
        var sla = AsArray(GetProp(report, "sla"));
        var table = TableAfterHeading(doc, "Disponibilidad plataforma Azure");
        if (table is not null && sla.Count > 0)
            FillTable(table, sla.Select(row => new object?[]
            {
                GetRaw(GetProp(row, "servicio")), GetRaw(GetProp(row, "acordado_h")), GetRaw(GetProp(row, "caidas_h")),
                GetRaw(GetProp(row, "disponibilidad")),
            }));

        var events = AsArray(GetProp(report, "caidas"));
        var eventsTable = TableAfterHeading(doc, "Detalle de caídas de servicio");
        if (eventsTable is not null)
        {
            if (events.Count > 0)
                FillTable(eventsTable, events.Select(ev => new object?[]
                {
                    "-", GetRaw(GetProp(ev, "inicio")), GetRaw(GetProp(ev, "fin")),
                    GetRaw(GetProp(ev, "servicios")), GetRaw(GetProp(ev, "causa")),
                }));
            else
                FillTable(eventsTable, new[] { new object?[] { "Sin novedades en el período", "-", "-", "-", "-" } });
        }
    }

    private static void FillConclusions(Doc doc, JsonElement report)
    {
        var narrative = GetProp(report, "narrative");
        FillListPlaceholders(doc, "Conclusión", AsArray(GetProp(narrative, "conclusiones")).Select(StringifyItem).ToList());
        FillListPlaceholders(doc, "Recomendación", AsArray(GetProp(narrative, "recomendaciones")).Select(StringifyItem).ToList());
    }

    private static void RemoveUnusedOptionalSections(Doc doc)
    {
        RemoveSection(doc, "Horario de encendido / apagado");
        RemoveSection(doc, "Reuniones realizadas");
        RemoveSection(doc, "Gráfica de requerimientos");
        RemoveSection(doc, "Cumplimiento de SLA");
    }

    // ---------- Estilo de párrafo ----------

    /// <summary>summary_para.style = doc.styles["Normal"]: fija el pStyle del párrafo al styleId del estilo dado.</summary>
    private static void SetParagraphStyle(Doc doc, Paragraph paragraph, string styleName)
    {
        var styleId = doc.MainPart.StyleDefinitionsPart?.Styles?
            .Elements<Style>()
            .FirstOrDefault(s => string.Equals(s.StyleName?.Val?.Value, styleName, StringComparison.Ordinal))?
            .StyleId?.Value ?? styleName;

        var pPr = paragraph.GetFirstChild<ParagraphProperties>();
        if (pPr is null)
        {
            pPr = new ParagraphProperties();
            paragraph.PrependChild(pPr);
        }
        var pStyle = pPr.GetFirstChild<ParagraphStyleId>();
        if (pStyle is null)
        {
            pStyle = new ParagraphStyleId();
            pPr.PrependChild(pStyle);
        }
        pStyle.Val = styleId;
    }

    // ---------- Carga de plantilla ----------

    /// <summary>
    /// Lee PLANTILLA-INF-CDC-BIT-v1.docx (equivale a Document(str(TEMPLATE_PATH))). Busca el archivo
    /// junto al ensamblado / output y en el árbol de código (Features/Reports/Templates), igual que
    /// ClosedXmlWafExporter.LoadTemplateBytes.
    /// RIESGO (igual que en WAF): para que el archivo se copie al output hay que añadir al .csproj
    /// &lt;Content Include="Features\Reports\Templates\PLANTILLA-INF-CDC-BIT-v1.docx"
    /// CopyToOutputDirectory="PreserveNewest" /&gt; (fuera del alcance de esta tarea: no se tocó el .csproj).
    /// </summary>
    private static byte[] LoadTemplateBytes()
    {
        var baseDir = AppContext.BaseDirectory;
        var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? baseDir;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Features", "Reports", "Templates", TemplateFileName),
            Path.Combine(baseDir, "Templates", TemplateFileName),
            Path.Combine(baseDir, TemplateFileName),
            Path.Combine(asmDir, "Features", "Reports", "Templates", TemplateFileName),
            Path.Combine(asmDir, "Templates", TemplateFileName),
            Path.Combine(asmDir, TemplateFileName),
        };
        foreach (var path in candidates)
            if (File.Exists(path))
                return File.ReadAllBytes(path);

        throw new FileNotFoundException(
            $"No se encontró la plantilla {TemplateFileName}. " +
            "Verifica que Features/Reports/Templates/PLANTILLA-INF-CDC-BIT-v1.docx se copie al output.");
    }

    // ---------- Lectura segura de JSON (equivalente a dict.get(...) del Python) ----------

    private static JsonElement GetProp(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)) return v;
        return default; // JsonValueKind.Undefined
    }

    private static List<JsonElement> AsArray(JsonElement el) =>
        el.ValueKind == JsonValueKind.Array ? el.EnumerateArray().ToList() : new List<JsonElement>();

    /// <summary>str(value) salvo None/ausente → null (equivale a dict.get(key) que devuelve None).</summary>
    private static string? GetStr(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String: return el.GetString();
            case JsonValueKind.Null:
            case JsonValueKind.Undefined: return null;
            default: return GetRaw(el);
        }
    }

    /// <summary>Representación de string del valor (números sin .0, igual que str(int) en Python); null si ausente.</summary>
    private static string? GetRaw(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String: return el.GetString();
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l)) return l.ToString(CultureInfo.InvariantCulture);
                return FormatPyNumber(el.GetDouble());
            case JsonValueKind.True: return "True";
            case JsonValueKind.False: return "False";
            case JsonValueKind.Null:
            case JsonValueKind.Undefined: return null;
            default: return el.GetRawText();
        }
    }

    private static int? GetInt(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var l)) return (int)l;
        if (el.ValueKind == JsonValueKind.Number) return (int)el.GetDouble();
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var i)) return i;
        return null;
    }

    private static double? GetDouble(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    /// <summary>str(item) para los items de conclusiones/recomendaciones (el Python hace str(i)).</summary>
    private static string StringifyItem(JsonElement el) => GetRaw(el) ?? "None";

    /// <summary>bool(value) del Python para resumen de alertas: objeto/array no vacío, número ≠0, string no vacío.</summary>
    private static bool IsFalsy(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => !el.EnumerateObject().Any(),
        JsonValueKind.Array => el.GetArrayLength() == 0,
        JsonValueKind.String => string.IsNullOrEmpty(el.GetString()),
        JsonValueKind.Number => (el.TryGetInt64(out var l) ? l == 0 : el.GetDouble() == 0),
        JsonValueKind.False => true,
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        _ => false,
    };

    /// <summary>
    /// str(round(x, 1)) de Python: un float redondeado a 1 decimal cuyo repr siempre muestra el
    /// decimal (30.0 → "30.0", 33.3 → "33.3").
    /// </summary>
    private static string PyRoundStr(double rounded) =>
        rounded.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Formatea un double como lo haría str() de Python: sin decimales si es entero.</summary>
    private static string FormatPyNumber(double d)
    {
        if (d == Math.Floor(d) && !double.IsInfinity(d))
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("0.0##############", CultureInfo.InvariantCulture);
    }

    private static int? ParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
