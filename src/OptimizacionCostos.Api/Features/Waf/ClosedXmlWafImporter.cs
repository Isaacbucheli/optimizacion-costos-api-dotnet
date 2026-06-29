using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Port fiel de app/waf/excel_importer.py (parse/score/deterministic_match/ai_match/preview) +
/// la lógica de apply de app/routes/waf.py::apply_waf_excel_import (que el FastAPI hace inline:
/// create_recommendation_from_matrix + refresh_manual_canonical_text + apply_tracking_update +
/// renumber_matrix_codes, todo en una transacción). El scoring reusa <see cref="WafText"/>; el
/// match IA reusa <see cref="IChatCompletionClient"/> + <see cref="AppConfig"/> (NO se construye
/// HttpClient nuevo). Los candidatos del cliente salen de <see cref="IWafCatalogStore"/>.
/// </summary>
public sealed partial class ClosedXmlWafImporter(
    ISqlConnectionFactory factory,
    IWafCatalogStore catalog,
    IChatCompletionClient chat,
    AppConfig config) : IWafExcelImporter
{
    // -------------------- Constantes (port de excel_importer.py) --------------------

    private const string SheetName = "Resultados";
    private const int XlsxMaxRows = 20_000;
    private const int MaxImportedResources = 200;

    private static readonly IReadOnlyDictionary<int, string> PillarTitles = new Dictionary<int, string>
    {
        [1] = "Eficiencia del rendimiento",
        [2] = "Excelencia operacional",
        [3] = "Seguridad",
        [4] = "Confiabilidad",
        [5] = "Optimizacion de costos",
    };

    private static readonly IReadOnlySet<string> NoDateValues = new HashSet<string>(StringComparer.Ordinal)
    {
        "", "-", "n/a", "na", "no aplica", "por definir", "pendiente", "sin definir",
    };

    // RESOURCE_SUMMARY_RE = ^\s*\d+\s+recursos?\s*$  (IGNORECASE)
    [GeneratedRegex(@"^\s*\d+\s+recursos?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceSummaryRe();

    // re.split(r"[\r\n;|]+") para _split_resources
    [GeneratedRegex(@"[\r\n;|]+")]
    private static partial Regex ResourceSplit();

    // _parse_code_and_title: ^\s*(\d+(?:\.\d+)*)\.?\s+(.+)$
    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)*)\.?\s+(.+)$")]
    private static partial Regex CodeAndTitle();

    // section_match: ^(\d)\s+(.+)$  (sobre normalized_first)
    [GeneratedRegex(@"^(\d)\s+(.+)$")]
    private static partial Regex SectionMatch();

    // _cell_text: re.sub control/zero-width -> espacio
    [GeneratedRegex("[\u0000-\u001F\u007F-\u009F\u200B-\u200D\uFEFF]")]
    private static partial Regex ControlChars();

    // _cell_text: re.sub [ \t]+ -> espacio
    [GeneratedRegex("[ \t]+")]
    private static partial Regex SpacesTabs();

    /// <summary>DEFAULT_COLUMN_MAP (posiciones de la plantilla estándar; fallback).</summary>
    private static readonly IReadOnlyDictionary<string, int> DefaultColumnMap = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["ambito"] = 0, ["review_date"] = 1, ["optimization_status"] = 2, ["resources"] = 3,
        ["benefit"] = 4, ["actions"] = 5, ["impact"] = 6, ["priority"] = 7,
        ["remediation_start_date"] = 8, ["projected_bit_effort"] = 9, ["completion"] = 10,
        ["execution_log"] = 11,
    };

    /// <summary>TRACKING_FIELDS: si todas están vacías, una fila con código de pilar es encabezado.</summary>
    private static readonly string[] TrackingFields =
    [
        "review_date", "optimization_status", "resources", "benefit", "actions",
        "impact", "priority", "remediation_start_date", "projected_bit_effort",
        "completion", "execution_log",
    ];

    // ============================================================================
    // 1. PARSE
    // ============================================================================

    public (IReadOnlyList<WafExcelRow> Rows, WafExcelParseMetadata Metadata) Parse(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var workbook = new XLWorkbook(stream);
        if (!workbook.Worksheets.TryGetWorksheet(SheetName, out var sheet))
            throw new InvalidOperationException("El Excel no contiene la hoja 'Resultados'.");

        var rows = new List<WafExcelRow>();
        int? currentPillar = null;
        var pillarSequence = new Dictionary<int, int>();
        var cols = new Dictionary<string, int>(DefaultColumnMap, StringComparer.Ordinal);
        var headerSeen = false;

        var lastRowUsed = sheet.LastRowUsed();
        var maxRow = lastRowUsed?.RowNumber() ?? 0;

        for (var rowIndex = 1; rowIndex <= maxRow; rowIndex++)
        {
            if (rowIndex > XlsxMaxRows)
                throw new InvalidOperationException($"El Excel supera el límite de {XlsxMaxRows} filas permitidas.");

            var rowObj = sheet.Row(rowIndex);
            var cells = ReadRowCells(rowObj);                 // valores crudos por columna (object?)
            var values = cells.Select(CellText).ToList();     // texto normalizado por columna
            var first = values.Count > 0 ? values[0] : "";
            var rowNumber = rowIndex;
            var normalizedFirst = WafText.NormalizeText(first);

            if (!headerSeen)
            {
                if (normalizedFirst.Contains("ambito de revision", StringComparison.Ordinal))
                {
                    headerSeen = true;
                    cols = BuildHeaderMap(cells);
                }
                continue;
            }

            // Encabezado de pilar: código 1..5 solo + todas las columnas de tracking vacías.
            var trackingEmpty = TrackingFields.All(key => ColText(cols, values, key).Length == 0);
            var section = SectionMatch().Match(normalizedFirst);
            if (section.Success
                && int.TryParse(section.Groups[1].Value, out var pillarNum)
                && PillarTitles.ContainsKey(pillarNum)
                && trackingEmpty)
            {
                currentPillar = pillarNum;
                continue;
            }
            if (string.IsNullOrEmpty(first) || currentPillar is null)
                continue;

            var (code, title) = ParseCodeAndTitle(first);
            var hasCode = !string.IsNullOrEmpty(code) && code.Contains('.', StringComparison.Ordinal);
            if (string.IsNullOrEmpty(title))
            {
                var desc = ColText(cols, values, "description");
                title = FirstLine(desc).Trim();
            }
            if (!hasCode)
            {
                if (string.IsNullOrEmpty(title) || trackingEmpty)
                    continue;
            }

            pillarSequence[currentPillar.Value] = pillarSequence.GetValueOrDefault(currentPillar.Value) + 1;
            if (!hasCode)
                code = $"{currentPillar.Value}.{pillarSequence[currentPillar.Value]}";

            var (reviewDate, reviewWarning) = ParseDate(ColRaw(cols, cells, "review_date"));
            var (remediationDate, remediationWarning) = ParseDate(ColRaw(cols, cells, "remediation_start_date"));
            var (completionPct, completionWarning) = ParseCompletion(ColRaw(cols, cells, "completion"));
            var warnings = new[] { reviewWarning, remediationWarning, completionWarning }
                .Where(w => !string.IsNullOrEmpty(w)).Select(w => w!).ToList();

            rows.Add(new WafExcelRow(
                RowNumber: rowNumber,
                PillarNumber: currentPillar,
                ExcelCode: code,
                Title: title,
                RawScope: first,
                ReviewDate: reviewDate,
                OptimizationStatus: ColText(cols, values, "optimization_status"),
                ResourceSummary: ColText(cols, values, "resources"),
                Benefit: ColText(cols, values, "benefit"),
                Actions: ColText(cols, values, "actions"),
                Impact: ColText(cols, values, "impact"),
                Priority: ColText(cols, values, "priority"),
                RemediationStartDate: remediationDate,
                ProjectedBitEffort: ColText(cols, values, "projected_bit_effort"),
                CompletionPct: completionPct,
                ExecutionLog: ColText(cols, values, "execution_log"),
                Resources: SplitResources(ColRaw(cols, cells, "resources")),
                Warnings: warnings));
        }

        var metadata = new WafExcelParseMetadata(
            Sheet: SheetName,
            RowsTotal: rows.Count,
            RowsWithWarnings: rows.Count(r => r.Warnings.Count > 0));
        return (rows, metadata);
    }

    /// <summary>Lee los valores crudos de una fila (objeto Cell.Value boxed) hasta su última celda usada.</summary>
    private static List<object?> ReadRowCells(IXLRow row)
    {
        var result = new List<object?>();
        var lastCell = row.LastCellUsed();
        var lastCol = lastCell?.Address.ColumnNumber ?? 0;
        for (var c = 1; c <= lastCol; c++)
        {
            var cell = row.Cell(c);
            result.Add(CellRawValue(cell));
        }
        return result;
    }

    /// <summary>
    /// Devuelve el valor "Python-equivalente" de la celda: DateTime para fechas, double para
    /// números, string para texto, null para vacío (lo que data_only=True entrega en openpyxl).
    /// </summary>
    private static object? CellRawValue(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        return cell.DataType switch
        {
            XLDataType.DateTime => cell.GetDateTime(),
            XLDataType.Number => cell.GetDouble(),
            XLDataType.Boolean => cell.GetBoolean(),
            _ => cell.GetString(),
        };
    }

    // -------------------- Helpers de parse (port 1:1) --------------------

    /// <summary>_cell_text: None→""; datetime→date.iso; number→texto; limpia controles y colapsa.</summary>
    private static string CellText(object? value)
    {
        switch (value)
        {
            case null:
                return "";
            case DateTime dt:
                return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case bool b:
                // str(True)/str(False) en Python → "True"/"False".
                return b ? "True" : "False";
            case double d:
                return NumberToText(d);
        }
        var text = (value.ToString() ?? "").Normalize(NormalizationForm.FormC);
        text = ControlChars().Replace(text, " ");
        text = SpacesTabs().Replace(text, " ");
        return text.Trim();
    }

    /// <summary>str(float) de Python: enteros sin ".0"; resto con repr corto.</summary>
    private static string NumberToText(double d)
    {
        if (d == Math.Floor(d) && !double.IsInfinity(d))
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>_classify_header: clasifica una celda de encabezado por tokens distintivos.</summary>
    private static string? ClassifyHeader(object? value)
    {
        var norm = WafText.NormalizeText(value);
        if (norm.Length == 0) return null;
        bool Has(string token) => norm.Contains(token, StringComparison.Ordinal);
        if (Has("ambito")) return "ambito";
        if (Has("descripcion")) return "description";
        if (Has("fecha") && Has("revision")) return "review_date";
        if (Has("fecha") && (Has("inicio") || Has("remediacion"))) return "remediation_start_date";
        if (Has("cumplimiento")) return "optimization_status";
        if (Has("recursos")) return "resources";
        if (Has("beneficio")) return "benefit";
        if (Has("acciones")) return "actions";
        if (Has("impacto")) return "impact";
        if (Has("prioridad")) return "priority";
        if (Has("esfuerzo")) return "projected_bit_effort";
        if (Has("registro") || Has("ejecucion")) return "execution_log";
        if (Has("estado")) return "completion";
        return null;
    }

    /// <summary>_build_header_map: confía en lo detectado si tiene "ambito" y ≥4 columnas; si no, default.</summary>
    private static Dictionary<string, int> BuildHeaderMap(IReadOnlyList<object?> cells)
    {
        var mapping = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var idx = 0; idx < cells.Count; idx++)
        {
            var key = ClassifyHeader(cells[idx]);
            if (key is not null && !mapping.ContainsKey(key))
                mapping[key] = idx;
        }
        if (mapping.ContainsKey("ambito") && mapping.Count >= 4)
            return mapping;
        return new Dictionary<string, int>(DefaultColumnMap, StringComparer.Ordinal);
    }

    private static string ColText(IReadOnlyDictionary<string, int> cols, IReadOnlyList<string> values, string key)
    {
        if (cols.TryGetValue(key, out var idx) && idx >= 0 && idx < values.Count)
            return values[idx];
        return "";
    }

    private static object? ColRaw(IReadOnlyDictionary<string, int> cols, IReadOnlyList<object?> cells, string key)
    {
        if (cols.TryGetValue(key, out var idx) && idx >= 0 && idx < cells.Count)
            return cells[idx];
        return null;
    }

    /// <summary>_parse_code_and_title: extrae (código, título) de la primera línea.</summary>
    private static (string Code, string Title) ParseCodeAndTitle(string value)
    {
        var firstLine = string.IsNullOrEmpty(value) ? "" : FirstLine(value);
        var match = CodeAndTitle().Match(firstLine.Trim());
        if (!match.Success)
        {
            var fallback = firstLine.Trim();
            if (fallback.Length == 0) fallback = value.Trim();
            return ("", fallback);
        }
        return (match.Groups[1].Value, match.Groups[2].Value.Trim());
    }

    private static string FirstLine(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var idx = value.IndexOfAny(['\r', '\n']);
        return idx < 0 ? value : value[..idx];
    }

    /// <summary>_parse_date: tolera datetime, 4 formatos textuales y valores literales "sin fecha".</summary>
    private static (string? Date, string? Warning) ParseDate(object? value)
    {
        switch (value)
        {
            case null:
                return (null, null);
            case DateTime dt:
                return (dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
        }
        var text = CellText(value);
        if (NoDateValues.Contains(WafText.NormalizeText(text)))
            return (null, null);
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "MM/dd/yyyy"];
        foreach (var fmt in formats)
        {
            if (DateTime.TryParseExact(text, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return (parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
        }
        return (null, $"Fecha no reconocida: {text}");
    }

    /// <summary>_parse_completion: %, decimales 0-1 (×100) o alias textuales; 0-100.</summary>
    private static (int? Pct, string? Warning) ParseCompletion(object? value)
    {
        if (value is null || CellText(value).Length == 0)
            return (null, "Estado vacio");

        double number;
        if (value is string raw)
        {
            var text = raw.Trim().Replace("%", "").Replace(",", ".");
            var aliases = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["optimizado"] = 100,
                ["completado"] = 100,
                ["cerrado"] = 100,
                ["parcialmente optimizado"] = 50,
                ["en proceso"] = 50,
                ["en progreso"] = 50,
                ["no optimizado"] = 0,
                ["pendiente"] = 0,
            };
            var normalized = WafText.NormalizeText(text);
            if (aliases.TryGetValue(normalized, out var alias))
                return (alias, null);
            if (!double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                return (null, $"Estado no numerico: {ValueToText(value)}");
        }
        else
        {
            // number/bool/datetime: el Python intenta float(raw); bool→1/0, datetime falla.
            switch (value)
            {
                case double d:
                    number = d;
                    break;
                case bool b:
                    number = b ? 1 : 0;
                    break;
                default:
                    return (null, $"Estado invalido: {ValueToText(value)}");
            }
        }

        if (double.IsNaN(number) || number < 0)
            return (null, $"Estado fuera de rango: {ValueToText(value)}");
        if (number <= 1) number *= 100;
        if (number > 100)
            return (null, $"Estado fuera de rango: {ValueToText(value)}");
        // int(round(number)): Python round() es banker's rounding (round-half-to-even).
        return ((int)Math.Round(number, MidpointRounding.ToEven), null);
    }

    /// <summary>str(value) para los mensajes de warning (paridad con f"...{value}").</summary>
    private static string ValueToText(object? value) => value switch
    {
        null => "",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        double d => NumberToText(d),
        _ => value.ToString() ?? "",
    };

    /// <summary>_split_resources: separa por \r\n;| ; descarta resúmenes "N recursos"; dedup; ≤200.</summary>
    private static List<string> SplitResources(object? rawValue)
    {
        var resources = new List<string>();
        if (rawValue is null) return resources;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var asText = ValueToRawString(rawValue);
        foreach (var part in ResourceSplit().Split(asText))
        {
            var name = CellText(part);
            if (name.Length == 0 || ResourceSummaryRe().IsMatch(name))
                continue;
            var key = name.ToLowerInvariant();
            if (!seen.Add(key))
                continue;
            resources.Add(name.Length > 512 ? name[..512] : name);
            if (resources.Count >= MaxImportedResources)
                break;
        }
        return resources;
    }

    /// <summary>str(raw_value) tal cual lo haría Python antes del re.split (no normaliza).</summary>
    private static string ValueToRawString(object value) => value switch
    {
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        double d => NumberToText(d),
        _ => value.ToString() ?? "",
    };

    // ============================================================================
    // 2. SCORING + MATCH
    // ============================================================================

    /// <summary>_candidate_text: concatena los campos del candidato para tokenizar/secuenciar.</summary>
    private static string CandidateText(WafCandidate c) =>
        string.Join(" ", new[]
        {
            CellText(c.MatrixCode), CellText(c.AdvisorName), CellText(c.AdvisorCategory),
            CellText(c.ReviewScopeEs), CellText(c.BenefitEs), CellText(c.ClientActionEs),
            CellText(c.BitActionEs), CellText(c.ResourcesText),
        });

    /// <summary>score_candidate: jaccard·0.46 + sequence·0.28 + resourceOverlap·0.14 + bonuses.</summary>
    private static (double Score, string Reason) ScoreCandidate(WafExcelRow row, WafCandidate candidate)
    {
        var rowText = string.Join(" ", row.Title, row.RawScope, row.ResourceSummary, row.Benefit, row.Actions);
        var candidateText = CandidateText(candidate);
        var rowTokens = WafText.Tokens(rowText);
        var candidateTokens = WafText.Tokens(candidateText);
        if (rowTokens.Count == 0 || candidateTokens.Count == 0)
            return (0.0, "sin tokens suficientes");

        var overlap = rowTokens.Count(candidateTokens.Contains);
        var union = rowTokens.Count + candidateTokens.Count - overlap;
        var jaccard = union > 0 ? (double)overlap / union : 0.0;

        var sequence = WafText.SequenceRatio(
            WafText.NormalizeText(row.Title), WafText.NormalizeText(candidateText));

        var resourceTokens = WafText.Tokens(row.ResourceSummary);
        var resourceOverlap = resourceTokens.Count > 0
            ? (double)resourceTokens.Count(candidateTokens.Contains) / Math.Max(1, resourceTokens.Count)
            : 0.0;

        var pillarBonus = row.PillarNumber is { } p && p > 0 && candidate.PillarNumber == p ? 0.12 : -0.18;
        var codeBonus = !string.IsNullOrEmpty(row.ExcelCode)
            && row.ExcelCode == (candidate.MatrixCode ?? "") ? 0.08 : 0.0;

        var score = Math.Max(0.0, Math.Min(1.0,
            (jaccard * 0.46) + (sequence * 0.28) + (resourceOverlap * 0.14) + pillarBonus + codeBonus));
        var rounded = Math.Round(score, 4, MidpointRounding.ToEven); // round(score, 4)
        var reason = string.Format(CultureInfo.InvariantCulture,
            "tokens {0}/{1}, similitud {2:0.00}, recursos {3:0.00}", overlap, union, sequence, resourceOverlap);
        return (rounded, reason);
    }

    /// <summary>Candidato puntuado tras deterministic_match (port del WafExcelCandidate de Python).</summary>
    private static WafExcelMatchCandidate ToMatchCandidate(WafCandidate c, double score, string reason) =>
        new(
            CanonicalId: c.CanonicalId,
            MatrixCode: c.MatrixCode ?? "",
            PillarNumber: c.PillarNumber,
            ReviewScopeEs: c.ReviewScopeEs ?? "",
            AdvisorName: c.AdvisorName ?? "",
            CurrentCompletionPct: c.CompletionPct ?? 0,
            CurrentRemediationStartDate: c.RemediationStartDate is { } d
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            CurrentExecutionLog: c.ExecutionLog ?? "",
            Score: score,
            Reason: reason);

    private sealed record DeterministicResult(
        WafCandidate? Match, IReadOnlyList<(WafCandidate Candidate, double Score, string Reason)> Scored, string Reason);

    /// <summary>deterministic_match: puntúa, ordena, exige score≥0.65 y margen≥0.12.</summary>
    private static DeterministicResult DeterministicMatch(WafExcelRow row, IReadOnlyList<WafCandidate> candidates)
    {
        var scored = new List<(WafCandidate Candidate, double Score, string Reason)>();
        foreach (var candidate in candidates)
        {
            var (score, reason) = ScoreCandidate(row, candidate);
            if (score <= 0) continue;
            scored.Add((candidate, score, reason));
        }
        scored = scored.OrderByDescending(item => item.Score).ToList();
        if (scored.Count == 0)
            return new DeterministicResult(null, [], "Sin candidatos");

        var best = scored[0];
        var margin = best.Score - (scored.Count > 1 ? scored[1].Score : 0.0);
        var top5 = scored.Take(5).ToList();
        if (best.Score >= 0.65 && margin >= 0.12)
            return new DeterministicResult(best.Candidate, top5,
                string.Format(CultureInfo.InvariantCulture,
                    "Match deterministico alto ({0:0.00}, margen {1:0.00})", best.Score, margin));
        return new DeterministicResult(null, top5,
            string.Format(CultureInfo.InvariantCulture,
                "Requiere validacion ({0:0.00}, margen {1:0.00})", best.Score, margin));
    }

    // -------------------- AI match (port de ai_match_excel_row) --------------------

    private sealed record AiMatch(int CanonicalId, double Confidence, string Reason);

    /// <summary>
    /// ai_match_excel_row: pide a Azure OpenAI mapear la fila contra los candidatos (top 5).
    /// Reusa IChatCompletionClient (no construye HTTP). Retry 3 intentos con backoff 2^attempt.
    /// Devuelve null si no está configurado, sin candidatos, o el modelo no confirma.
    /// </summary>
    private async Task<AiMatch?> AiMatchExcelRowAsync(
        WafExcelRow row, IReadOnlyList<(WafCandidate Candidate, double Score, string Reason)> candidates,
        CancellationToken ct)
    {
        if (!IsAiConfigured() || candidates.Count == 0)
            return null;

        var allowedIds = candidates.Select(c => c.Candidate.CanonicalId).ToHashSet();

        var options = candidates.Take(5).Select(item => new
        {
            canonical_id = item.Candidate.CanonicalId,
            matrix_code = item.Candidate.MatrixCode ?? "",
            pillar_number = (int)item.Candidate.PillarNumber,
            review_scope_es = item.Candidate.ReviewScopeEs ?? "",
            advisor_name = item.Candidate.AdvisorName ?? "",
            score = item.Score,
        }).ToArray();

        var optionsJson = JsonSerializer.Serialize(options, JsonOpts);
        var prompt = BuildAiMatchPrompt(row, optionsJson);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var content = await Task.Run(() => chat.Complete("", prompt), ct);
                if (string.IsNullOrWhiteSpace(content))
                    throw new InvalidOperationException("respuesta IA vacia");

                using var doc = WafAiJson.ParseDedup(content);
                var rootEl = doc.RootElement;

                if (!rootEl.GetBoolTruthy("match"))
                    return null;

                var canonicalId = rootEl.GetIntOrZero("canonical_id");
                if (!allowedIds.Contains(canonicalId))
                    return null;

                var confidence = Math.Max(0.0, Math.Min(1.0, rootEl.GetDoubleOrNull("confidence") ?? 0.0));
                var reasonRaw = rootEl.GetString("reason");
                var reason = string.IsNullOrEmpty(reasonRaw) ? "Match sugerido por Azure OpenAI" : reasonRaw;
                if (reason.Length > 500) reason = reason[..500];

                return new AiMatch(canonicalId, confidence, reason);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                if (attempt == 2) return null;
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct); }
                catch (OperationCanceledException) { throw; }
            }
        }
        return null;
    }

    /// <summary>Prompt EXACTO de ai_match_excel_row (texto en español, mismo orden y wording).</summary>
    private static string BuildAiMatchPrompt(WafExcelRow row, string optionsJson)
    {
        var sb = new StringBuilder();
        sb.Append("Eres un consultor Azure. Debes mapear una fila historica de una matriz WAF contra recomendaciones existentes.\n");
        sb.Append("No inventes recomendaciones nuevas. Si ningun candidato representa la misma recomendacion, devuelve match=false.\n");
        sb.Append("Responde solo JSON valido.\n\n");
        sb.Append("Fila Excel:\n");
        sb.Append("- pilar: ").Append(row.PillarNumber?.ToString(CultureInfo.InvariantCulture) ?? "None").Append('\n');
        sb.Append("- titulo: ").Append(row.Title).Append('\n');
        sb.Append("- ambito completo: ").Append(row.RawScope).Append('\n');
        sb.Append("- recursos: ").Append(row.ResourceSummary).Append('\n');
        sb.Append("- beneficio: ").Append(row.Benefit).Append('\n');
        sb.Append("- acciones: ").Append(row.Actions).Append("\n\n");
        sb.Append("Candidatos existentes:\n");
        sb.Append(optionsJson).Append("\n\n");
        sb.Append("JSON requerido:\n");
        sb.Append("{\"match\": true, \"canonical_id\": 123, \"confidence\": 0.0, \"reason\": \"\"}");
        return sb.ToString();
    }

    // ============================================================================
    // 3. PREVIEW
    // ============================================================================

    public async Task<IReadOnlyList<WafExcelPreviewItem>> PreviewAsync(
        int clientId, IReadOnlyList<WafExcelRow> rows, bool useAi, int maxAiCalls, CancellationToken ct = default)
    {
        var candidates = await LoadCandidatesAsync(clientId, ct);

        var previews = new List<WafExcelPreviewItem>(rows.Count);
        var aiCalls = 0;

        foreach (var row in rows)
        {
            var det = DeterministicMatch(row, candidates);
            WafCandidate? match = det.Match;
            var matchSource = match is not null ? "deterministic" : null;
            var confidence = det.Scored.Count > 0 ? det.Scored[0].Score : 0.0;
            var reason = det.Reason;

            if (match is null && useAi && aiCalls < maxAiCalls
                && det.Scored.Count > 0 && det.Scored[0].Score >= 0.28)
            {
                aiCalls++;
                var aiResult = await AiMatchExcelRowAsync(row, det.Scored, ct);
                if (aiResult is not null && aiResult.Confidence >= 0.72)
                {
                    match = candidates.First(c => c.CanonicalId == aiResult.CanonicalId);
                    matchSource = "azure_openai";
                    confidence = aiResult.Confidence;
                    reason = aiResult.Reason;
                }
            }
            else if (match is null && useAi && det.Scored.Count > 0
                && det.Scored[0].Score >= 0.28 && aiCalls >= maxAiCalls)
            {
                reason = $"{reason}; limite de validaciones Azure OpenAI alcanzado";
            }

            string status;
            if (match is not null)
                status = row.CompletionPct is not null ? "matched" : "needs_review";
            else if (det.Scored.Count > 0)
                status = "needs_review";
            else
                status = "new";

            if (row.CompletionPct is null)
                reason = JoinReason(reason, "Estado/avance invalido");

            var trackingUpdates = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (row.CompletionPct is not null)
                trackingUpdates["completion_pct"] = row.CompletionPct.Value;
            if (!string.IsNullOrEmpty(row.RemediationStartDate))
                trackingUpdates["remediation_start_date"] = row.RemediationStartDate;
            if (!string.IsNullOrEmpty(row.ExecutionLog))
                trackingUpdates["execution_log"] = row.ExecutionLog;

            WafExcelSuggestedMatch? suggested = match is null ? null : new WafExcelSuggestedMatch(
                CanonicalId: match.CanonicalId,
                MatrixCode: match.MatrixCode ?? "",
                PillarNumber: match.PillarNumber,
                ReviewScopeEs: match.ReviewScopeEs ?? "",
                AdvisorName: match.AdvisorName ?? "",
                CurrentCompletionPct: match.CompletionPct ?? 0,
                CurrentRemediationStartDate: match.RemediationStartDate is { } d
                    ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                CurrentExecutionLog: match.ExecutionLog ?? "");

            var candidateDtos = det.Scored
                .Select(s => ToMatchCandidate(s.Candidate, s.Score, s.Reason))
                .ToList();

            previews.Add(new WafExcelPreviewItem(
                Row: row,
                Status: status,
                CanCreate: row.PillarNumber is not null && !string.IsNullOrEmpty(row.Title),
                MatchSource: matchSource,
                Confidence: Math.Round(confidence, 4, MidpointRounding.ToEven),
                Reason: reason,
                SuggestedMatch: suggested,
                Candidates: candidateDtos,
                TrackingUpdates: trackingUpdates));
        }
        return previews;
    }

    /// <summary>"; ".join([reason, extra]).strip("; ") del Python.</summary>
    private static string JoinReason(string reason, string extra)
    {
        var joined = string.Join("; ", new[] { reason, extra });
        return joined.Trim(';', ' ');
    }

    /// <summary>Carga los candidatos del cliente (catálogo activo) reusando el store de catálogo.</summary>
    private async Task<IReadOnlyList<WafCandidate>> LoadCandidatesAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var candidates = await catalog.LoadClientCandidatesAsync(conn, tx, clientId, ct);
            await tx.CommitAsync(ct);
            return candidates;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ============================================================================
    // 4. APPLY (port de apply_waf_excel_import + helpers de ingestion.py / tracking.py)
    // ============================================================================

    private static readonly string[] PersistableTrackingFields =
        ["completion_pct", "remediation_start_date", "projected_bit_effort", "execution_log"];

    private static readonly IReadOnlyDictionary<int, string> PillarLabels = new Dictionary<int, string>
    {
        [1] = "Eficiencia del rendimiento",
        [2] = "Excelencia operacional",
        [3] = "Seguridad",
        [4] = "Confiabilidad",
        [5] = "Optimizacion de costos",
    };

    private const string ManualSubscriptionId = "importado";

    public async Task<WafExcelApplyResult> ApplyAsync(
        int clientId, IReadOnlyList<WafExcelApplyItem> items, string? actor, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var applied = 0;
            var created = 0;
            var skipped = 0;
            var changedFields = new Dictionary<string, int>(StringComparer.Ordinal);
            var errors = new List<WafExcelApplyError>();
            var now = DateTime.UtcNow;

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (!item.Approved) { skipped++; continue; }

                int canonicalId;
                if (item.Action == "create")
                {
                    try
                    {
                        if (item.PillarNumber is null || string.IsNullOrWhiteSpace(item.Title))
                            throw new ArgumentException("pillar_number y title son requeridos para crear una recomendacion.");
                        var (cid, _) = await CreateRecommendationFromMatrixAsync(
                            conn, tx, clientId,
                            pillarNumber: item.PillarNumber.Value,
                            title: item.Title!,
                            reviewScope: item.ReviewScope,
                            benefit: item.Benefit,
                            clientAction: item.Actions,
                            bitAction: null,
                            impact: item.Impact,
                            resources: item.Resources,
                            now: now, ct: ct);
                        canonicalId = cid;
                    }
                    catch (ArgumentException ex)
                    {
                        errors.Add(new WafExcelApplyError(item.RowNumber, ex.Message));
                        skipped++;
                        continue;
                    }
                    created++;
                }
                else
                {
                    if (item.CanonicalId is null)
                    {
                        errors.Add(new WafExcelApplyError(item.RowNumber, "Recommendation not found"));
                        skipped++;
                        continue;
                    }
                    canonicalId = item.CanonicalId.Value;
                    if (!await RecommendationExistsAsync(conn, tx, clientId, canonicalId, ct))
                    {
                        errors.Add(new WafExcelApplyError(item.RowNumber, "Recommendation not found"));
                        skipped++;
                        continue;
                    }
                    // Re-import: corrige separación CLIENTE / BUSINESS IT de manuales existentes.
                    if (item.Actions is not null)
                        await RefreshManualCanonicalTextAsync(conn, tx, canonicalId, clientAction: item.Actions, ct);
                }

                // values: solo claves presentes y no nulas (paridad con el armado del Python).
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (item.CompletionPct is not null)
                    values["completion_pct"] = item.CompletionPct.Value;
                if (item.RemediationStartDate is not null)
                    values["remediation_start_date"] = item.RemediationStartDate.Value;
                if (item.ExecutionLog is not null)
                {
                    var log = item.ExecutionLog.Trim();
                    values["execution_log"] = log.Length > 0 ? log : null; // .strip() or None
                }
                if (item.Action == "create" && item.ProjectedBitEffort is not null)
                {
                    var effort = item.ProjectedBitEffort.Trim();
                    values["projected_bit_effort"] = effort.Length > 0 ? effort : null;
                }

                if (values.Count > 0)
                {
                    var changed = await ApplyTrackingUpdateAsync(conn, tx, clientId, canonicalId, values, actor, ct);
                    foreach (var field in changed)
                        changedFields[field] = changedFields.GetValueOrDefault(field) + 1;
                    if (item.Action != "create")
                        applied++;
                }
                else if (item.Action != "create")
                {
                    skipped++;
                }
            }

            await RenumberMatrixCodesAsync(conn, tx, clientId, ct);
            await tx.CommitAsync(ct);

            return new WafExcelApplyResult(
                RowsApplied: applied,
                RowsCreated: created,
                RowsSkipped: skipped,
                ChangedFields: changedFields,
                Errors: errors);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // -------------------- apply helpers (port 1:1 SQL) --------------------

    private static async Task<bool> RecommendationExistsAsync(
        SqlConnection conn, SqlTransaction tx, int clientId, int canonicalId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT 1
            FROM dbo.waf_recommendation
            WHERE client_id = @client_id AND canonical_id = @canonical_id
              AND COALESCE(is_dismissed, 0) = 0
            """;
        cmd.Parameters.Add(new SqlParameter("@client_id", clientId));
        cmd.Parameters.Add(new SqlParameter("@canonical_id", canonicalId));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Port de create_recommendation_from_matrix (ingestion.py): canónica + recomendación + findings.</summary>
    private static async Task<(int CanonicalId, bool Created)> CreateRecommendationFromMatrixAsync(
        SqlConnection conn, SqlTransaction tx, int clientId,
        int pillarNumber, string title, string? reviewScope, string? benefit,
        string? clientAction, string? bitAction, string? impact,
        IReadOnlyList<string>? resources, DateTime now, CancellationToken ct)
    {
        if (!PillarLabels.ContainsKey(pillarNumber))
            throw new ArgumentException("pillar_number debe estar entre 1 y 5");

        var advisorName = Truncate(NonEmpty(Normalize(title), "Recomendacion sin titulo"), 500);
        var advisorCategory = Truncate($"Matriz historica - {PillarLabels[pillarNumber]}", 100);
        var scope = Truncate(NonEmpty(Normalize(reviewScope), advisorName), 1000);
        var benefitEs = Truncate(NonEmpty(Normalize(benefit),
            "Importado desde matriz historica. Pendiente de revision funcional."), 2000);
        var (clientText, bitText) = ResolveActions(clientAction, bitAction);
        var clientActionEs = Truncate(NonEmpty(clientText,
            "Validar aplicabilidad y acordar el plan de remediacion."), 2000);
        var bitActionEs = Truncate(NonEmpty(bitText,
            "Revisar el hallazgo, completar el contexto tecnico y proponer acciones."), 2000);

        int canonicalId;
        // Match exacto por firma (advisor_name, advisor_category) case-insensitive.
        await using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = """
                SELECT canonical_id
                FROM dbo.waf_recommendation_canonical
                WHERE LOWER(advisor_name) = LOWER(@name) AND LOWER(advisor_category) = LOWER(@category)
                """;
            find.Parameters.Add(new SqlParameter("@name", advisorName));
            find.Parameters.Add(new SqlParameter("@category", advisorCategory));
            var found = await find.ExecuteScalarAsync(ct);
            if (found is not null and not DBNull)
            {
                canonicalId = Convert.ToInt32(found);
                // El Excel es la fuente de verdad: refresca su texto.
                await using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = """
                    UPDATE dbo.waf_recommendation_canonical
                    SET review_scope_es = @scope, benefit_es = @benefit,
                        client_action_es = @clientAction, bit_action_es = @bitAction,
                        updated_at = SYSUTCDATETIME()
                    WHERE canonical_id = @id
                    """;
                upd.Parameters.Add(new SqlParameter("@scope", scope));
                upd.Parameters.Add(new SqlParameter("@benefit", benefitEs));
                upd.Parameters.Add(new SqlParameter("@clientAction", clientActionEs));
                upd.Parameters.Add(new SqlParameter("@bitAction", bitActionEs));
                upd.Parameters.Add(new SqlParameter("@id", canonicalId));
                await upd.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO dbo.waf_recommendation_canonical (
                        advisor_name, advisor_category, pillar_number,
                        review_scope_es, benefit_es, client_action_es, bit_action_es,
                        ai_review_status
                    )
                    OUTPUT INSERTED.canonical_id
                    VALUES (@name, @category, @pillar, @scope, @benefit, @clientAction, @bitAction, 'pending')
                    """;
                ins.Parameters.Add(new SqlParameter("@name", advisorName));
                ins.Parameters.Add(new SqlParameter("@category", advisorCategory));
                ins.Parameters.Add(new SqlParameter("@pillar", pillarNumber));
                ins.Parameters.Add(new SqlParameter("@scope", scope));
                ins.Parameters.Add(new SqlParameter("@benefit", benefitEs));
                ins.Parameters.Add(new SqlParameter("@clientAction", clientActionEs));
                ins.Parameters.Add(new SqlParameter("@bitAction", bitActionEs));
                canonicalId = Convert.ToInt32(await ins.ExecuteScalarAsync(ct));
            }
        }

        var (businessImpact, impactNumber) = ImpactFromText(impact);

        int recommendationId;
        await using (var findRec = conn.CreateCommand())
        {
            findRec.Transaction = tx;
            findRec.CommandText = """
                SELECT recommendation_id
                FROM dbo.waf_recommendation
                WHERE client_id = @client_id AND canonical_id = @canonical_id
                """;
            findRec.Parameters.Add(new SqlParameter("@client_id", clientId));
            findRec.Parameters.Add(new SqlParameter("@canonical_id", canonicalId));
            var existing = await findRec.ExecuteScalarAsync(ct);
            if (existing is not null and not DBNull)
            {
                recommendationId = Convert.ToInt32(existing);
                await using var updRec = conn.CreateCommand();
                updRec.Transaction = tx;
                updRec.CommandText = """
                    UPDATE dbo.waf_recommendation
                    SET business_impact = @impact, impact_number = @impactNumber, last_seen_at = @now,
                        is_active = 1, is_dismissed = 0, dismissed_at = NULL,
                        dismissed_by = NULL, dismissal_reason = NULL
                    WHERE recommendation_id = @id
                    """;
                updRec.Parameters.Add(new SqlParameter("@impact", businessImpact));
                updRec.Parameters.Add(new SqlParameter("@impactNumber", impactNumber));
                updRec.Parameters.Add(new SqlParameter("@now", now));
                updRec.Parameters.Add(new SqlParameter("@id", recommendationId));
                await updRec.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var insRec = conn.CreateCommand();
                insRec.Transaction = tx;
                insRec.CommandText = """
                    INSERT INTO dbo.waf_recommendation (
                        client_id, canonical_id, matrix_code, business_impact,
                        impact_number, first_seen_at, last_seen_at
                    )
                    OUTPUT INSERTED.recommendation_id
                    VALUES (@client_id, @canonical_id, @code, @impact, @impactNumber, @now, @now)
                    """;
                insRec.Parameters.Add(new SqlParameter("@client_id", clientId));
                insRec.Parameters.Add(new SqlParameter("@canonical_id", canonicalId));
                insRec.Parameters.Add(new SqlParameter("@code", MatrixCode(pillarNumber, advisorName, advisorCategory)));
                insRec.Parameters.Add(new SqlParameter("@impact", businessImpact));
                insRec.Parameters.Add(new SqlParameter("@impactNumber", impactNumber));
                insRec.Parameters.Add(new SqlParameter("@now", now));
                recommendationId = Convert.ToInt32(await insRec.ExecuteScalarAsync(ct));
            }
        }

        await CreateManualFindingsAsync(conn, tx, recommendationId, resources, businessImpact, now, ct);

        // Recalcula resource_count desde findings activos.
        await using (var recalc = conn.CreateCommand())
        {
            recalc.Transaction = tx;
            recalc.CommandText = """
                UPDATE dbo.waf_recommendation
                SET resource_count = (
                    SELECT COUNT(*)
                    FROM dbo.waf_resource_finding f
                    WHERE f.recommendation_id = dbo.waf_recommendation.recommendation_id
                      AND f.status = 'active'
                )
                WHERE recommendation_id = @id
                """;
            recalc.Parameters.Add(new SqlParameter("@id", recommendationId));
            await recalc.ExecuteNonQueryAsync(ct);
        }

        return (canonicalId, true);
    }

    /// <summary>Port de _create_manual_findings: findings 'a mano' con metadatos placeholder; dedup por (rec, name, sub).</summary>
    private static async Task CreateManualFindingsAsync(
        SqlConnection conn, SqlTransaction tx, int recommendationId,
        IReadOnlyList<string>? resources, string businessImpact, DateTime now, CancellationToken ct)
    {
        foreach (var rawName in resources ?? [])
        {
            var name = Truncate(Normalize(rawName), 512);
            if (name.Length == 0) continue;

            await using (var dup = conn.CreateCommand())
            {
                dup.Transaction = tx;
                dup.CommandText = """
                    SELECT TOP 1 finding_id
                    FROM dbo.waf_resource_finding
                    WHERE recommendation_id = @rec AND resource_name = @name AND subscription_id = @sub
                    """;
                dup.Parameters.Add(new SqlParameter("@rec", recommendationId));
                dup.Parameters.Add(new SqlParameter("@name", name));
                dup.Parameters.Add(new SqlParameter("@sub", ManualSubscriptionId));
                if (await dup.ExecuteScalarAsync(ct) is not null and not DBNull)
                    continue;
            }

            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO dbo.waf_resource_finding (
                    recommendation_id, resource_name, resource_type, resource_group,
                    subscription_id, subscription_name, azure_resource_id, business_impact,
                    additional_info, first_seen_at, last_seen_at
                )
                VALUES (@rec, @name, '(importado)', '(importado)', @sub, '(matriz historica)',
                        NULL, @impact, NULL, @now, @now)
                """;
            ins.Parameters.Add(new SqlParameter("@rec", recommendationId));
            ins.Parameters.Add(new SqlParameter("@name", name));
            ins.Parameters.Add(new SqlParameter("@sub", ManualSubscriptionId));
            ins.Parameters.Add(new SqlParameter("@impact", businessImpact));
            ins.Parameters.Add(new SqlParameter("@now", now));
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Port de refresh_manual_canonical_text: refresca acciones solo si es canónica manual.</summary>
    private static async Task<bool> RefreshManualCanonicalTextAsync(
        SqlConnection conn, SqlTransaction tx, int canonicalId, string? clientAction, CancellationToken ct)
    {
        string? category;
        await using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT advisor_category FROM dbo.waf_recommendation_canonical WHERE canonical_id = @id";
            find.Parameters.Add(new SqlParameter("@id", canonicalId));
            var result = await find.ExecuteScalarAsync(ct);
            category = result is null or DBNull ? null : result.ToString();
        }
        if (string.IsNullOrEmpty(category)
            || !category.ToLowerInvariant().StartsWith("matriz historica", StringComparison.Ordinal))
            return false;

        var (clientText, bitText) = ResolveActions(clientAction, null);
        await using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = """
            UPDATE dbo.waf_recommendation_canonical
            SET client_action_es = @clientAction, bit_action_es = @bitAction, updated_at = SYSUTCDATETIME()
            WHERE canonical_id = @id
            """;
        upd.Parameters.Add(new SqlParameter("@clientAction",
            Truncate(NonEmpty(clientText, "Validar aplicabilidad y acordar el plan de remediacion."), 2000)));
        upd.Parameters.Add(new SqlParameter("@bitAction",
            Truncate(NonEmpty(bitText, "Revisar el hallazgo, completar el contexto tecnico y proponer acciones."), 2000)));
        upd.Parameters.Add(new SqlParameter("@id", canonicalId));
        await upd.ExecuteNonQueryAsync(ct);
        return true;
    }

    /// <summary>Port de apply_tracking_update (tracking.py): MERGE upsert + historia por campo cambiado.</summary>
    private static async Task<IReadOnlyList<string>> ApplyTrackingUpdateAsync(
        SqlConnection conn, SqlTransaction tx, int clientId, int canonicalId,
        IReadOnlyDictionary<string, object?> values, string? updatedBy, CancellationToken ct)
    {
        // load_tracking (solo los 4 campos que el apply puede tocar + los demás con default).
        var current = await LoadTrackingAsync(conn, tx, clientId, canonicalId, ct);

        // clean_values: solo claves en TRACKING_FIELDS (las 4 que el apply envía siempre lo están).
        var merged = new Dictionary<string, object?>(current, StringComparer.Ordinal);
        foreach (var kv in values)
            if (PersistableTrackingFields.Contains(kv.Key)
                || kv.Key is "priority_override" or "internal_notes")
                merged[kv.Key] = kv.Value;

        await using (var mergeCmd = conn.CreateCommand())
        {
            mergeCmd.Transaction = tx;
            mergeCmd.CommandText = """
                MERGE dbo.waf_recommendation_tracking AS target
                USING (SELECT @client_id AS client_id, @canonical_id AS canonical_id) AS source
                ON target.client_id = source.client_id AND target.canonical_id = source.canonical_id
                WHEN MATCHED THEN
                    UPDATE SET completion_pct = @completion_pct, remediation_start_date = @remediation_start_date,
                        projected_bit_effort = @projected_bit_effort, execution_log = @execution_log,
                        priority_override = @priority_override, internal_notes = @internal_notes,
                        updated_at = SYSUTCDATETIME(), updated_by = @updated_by
                WHEN NOT MATCHED THEN
                    INSERT (client_id, canonical_id, completion_pct, remediation_start_date,
                            projected_bit_effort, execution_log, priority_override,
                            internal_notes, updated_by)
                    VALUES (@client_id, @canonical_id, @completion_pct, @remediation_start_date,
                            @projected_bit_effort, @execution_log, @priority_override,
                            @internal_notes, @updated_by);
                """;
            mergeCmd.Parameters.Add(new SqlParameter("@client_id", clientId));
            mergeCmd.Parameters.Add(new SqlParameter("@canonical_id", canonicalId));
            mergeCmd.Parameters.Add(new SqlParameter("@completion_pct", ToDb(merged.GetValueOrDefault("completion_pct")) ?? 0));
            mergeCmd.Parameters.Add(new SqlParameter("@remediation_start_date", DateParam(merged.GetValueOrDefault("remediation_start_date"))));
            mergeCmd.Parameters.Add(new SqlParameter("@projected_bit_effort", ToDb(merged.GetValueOrDefault("projected_bit_effort"))));
            mergeCmd.Parameters.Add(new SqlParameter("@execution_log", ToDb(merged.GetValueOrDefault("execution_log"))));
            mergeCmd.Parameters.Add(new SqlParameter("@priority_override", ToDb(merged.GetValueOrDefault("priority_override"))));
            mergeCmd.Parameters.Add(new SqlParameter("@internal_notes", ToDb(merged.GetValueOrDefault("internal_notes"))));
            mergeCmd.Parameters.Add(new SqlParameter("@updated_by", (object?)updatedBy ?? DBNull.Value));
            await mergeCmd.ExecuteNonQueryAsync(ct);
        }

        var changed = new List<string>();
        foreach (var kv in values)
        {
            if (!(PersistableTrackingFields.Contains(kv.Key)
                  || kv.Key is "priority_override" or "internal_notes"))
                continue; // clean_values
            var oldValue = current.GetValueOrDefault(kv.Key);
            if (TrackingEquals(oldValue, kv.Value)) continue;
            changed.Add(kv.Key);

            await using var hist = conn.CreateCommand();
            hist.Transaction = tx;
            hist.CommandText = """
                INSERT INTO dbo.waf_tracking_history (
                    client_id, canonical_id, field_changed, old_value, new_value, changed_by
                )
                VALUES (@client_id, @canonical_id, @field, @old_value, @new_value, @changed_by)
                """;
            hist.Parameters.Add(new SqlParameter("@client_id", clientId));
            hist.Parameters.Add(new SqlParameter("@canonical_id", canonicalId));
            hist.Parameters.Add(new SqlParameter("@field", kv.Key));
            hist.Parameters.Add(new SqlParameter("@old_value", (object?)TrackingString(oldValue) ?? DBNull.Value));
            hist.Parameters.Add(new SqlParameter("@new_value", (object?)TrackingString(kv.Value) ?? DBNull.Value));
            hist.Parameters.Add(new SqlParameter("@changed_by", (object?)updatedBy ?? DBNull.Value));
            await hist.ExecuteNonQueryAsync(ct);
        }
        return changed;
    }

    /// <summary>load_tracking: estado actual (4 campos relevantes) o defaults.</summary>
    private static async Task<Dictionary<string, object?>> LoadTrackingAsync(
        SqlConnection conn, SqlTransaction tx, int clientId, int canonicalId, CancellationToken ct)
    {
        var current = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["completion_pct"] = 0,
            ["remediation_start_date"] = null,
            ["projected_bit_effort"] = null,
            ["execution_log"] = null,
            ["priority_override"] = null,
            ["internal_notes"] = null,
        };
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT completion_pct, remediation_start_date, projected_bit_effort,
                   execution_log, priority_override, internal_notes
            FROM dbo.waf_recommendation_tracking
            WHERE client_id = @client_id AND canonical_id = @canonical_id
            """;
        cmd.Parameters.Add(new SqlParameter("@client_id", clientId));
        cmd.Parameters.Add(new SqlParameter("@canonical_id", canonicalId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            current["completion_pct"] = r.IsDBNull(0) ? 0 : r.GetInt32(0);
            current["remediation_start_date"] = r.IsDBNull(1) ? null : DateOnly.FromDateTime(r.GetDateTime(1));
            current["projected_bit_effort"] = r.IsDBNull(2) ? null : r.GetString(2);
            current["execution_log"] = r.IsDBNull(3) ? null : r.GetString(3);
            current["priority_override"] = r.IsDBNull(4) ? null : (int)r.GetByte(4);
            current["internal_notes"] = r.IsDBNull(5) ? null : r.GetString(5);
        }
        return current;
    }

    /// <summary>renumber_matrix_codes: matrix_code = 'pilar.secuencia' por ROW_NUMBER.</summary>
    private static async Task RenumberMatrixCodesAsync(
        SqlConnection conn, SqlTransaction tx, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            WITH ranked AS (
                SELECT r.recommendation_id,
                       CONCAT(c.pillar_number, '.', CAST(ROW_NUMBER() OVER (
                           PARTITION BY c.pillar_number
                           ORDER BY r.impact_number, c.review_scope_es, r.canonical_id
                       ) AS varchar(10))) AS code
                FROM dbo.waf_recommendation r
                INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = r.canonical_id
                WHERE r.client_id = @client_id
                  AND r.is_active = 1
                  AND COALESCE(r.is_dismissed, 0) = 0
            )
            UPDATE r
            SET r.matrix_code = ranked.code
            FROM dbo.waf_recommendation r
            INNER JOIN ranked ON ranked.recommendation_id = r.recommendation_id
            """;
        cmd.Parameters.Add(new SqlParameter("@client_id", clientId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------- helpers de texto/impacto (port de ingestion.py) --------------------

    /// <summary>_normalize: " ".join(str(value).strip().split()).</summary>
    private static string Normalize(string? value) =>
        string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NonEmpty(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;

    // _ACTION_LABEL_RE = (CLIENTE|BUSINESS\s*[-_]?\s*IT|BIT)\s*:  (IGNORECASE)
    [GeneratedRegex(@"(CLIENTE|BUSINESS\s*[-_]?\s*IT|BIT)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ActionLabelRe();

    [GeneratedRegex(@"[\s\-_]+")]
    private static partial Regex LabelStrip();

    /// <summary>split_actions: separa la celda en (acción_cliente, acción_bit) por etiquetas de rol.</summary>
    private static (string Client, string Bit) SplitActions(string? text)
    {
        var raw = Normalize(text);
        if (raw.Length == 0) return ("", "");
        var matches = ActionLabelRe().Matches(raw);
        if (matches.Count == 0) return (raw, "");

        var clientParts = new List<string>();
        var bitParts = new List<string>();
        var lead = raw[..matches[0].Index].Trim(' ', '-', ':');
        if (lead.Length > 0) clientParts.Add(lead);

        for (var index = 0; index < matches.Count; index++)
        {
            var label = LabelStrip().Replace(matches[index].Groups[1].Value, "").ToUpperInvariant();
            var isBit = label is "BUSINESSIT" or "BIT";
            var start = matches[index].Index + matches[index].Length;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : raw.Length;
            var segment = raw[start..end].Trim(' ', '-', ':');
            if (segment.Length == 0) continue;
            (isBit ? bitParts : clientParts).Add(segment);
        }
        return (string.Join(" ", clientParts).Trim(), string.Join(" ", bitParts).Trim());
    }

    /// <summary>_resolve_actions: usa bit_action explícito si viene; si no, separa por etiquetas.</summary>
    private static (string Client, string Bit) ResolveActions(string? clientAction, string? bitAction)
    {
        if (Normalize(bitAction).Length > 0)
            return (Normalize(clientAction), Normalize(bitAction));
        return SplitActions(clientAction);
    }

    /// <summary>_impact_from_text: '1 - ALTA' etc. → (business_impact, impact_number). Default Medium/2.</summary>
    private static (string BusinessImpact, int ImpactNumber) ImpactFromText(string? value)
    {
        var text = Normalize(value).ToLowerInvariant();
        if (text.Length == 0) return ("Medium", 2);
        if (text.StartsWith('1') || text.Contains("alta") || text.Contains("alto") || text.Contains("high"))
            return ("High", 1);
        if (text.StartsWith('3') || text.Contains("baja") || text.Contains("bajo") || text.Contains("low"))
            return ("Low", 3);
        return ("Medium", 2);
    }

    /// <summary>_matrix_code: 'AUTO-{pillar}-{SHA1(name|category)[:8].upper}'.</summary>
    private static string MatrixCode(int pillar, string advisorName, string advisorCategory)
    {
        var bytes = Encoding.UTF8.GetBytes($"{advisorName}|{advisorCategory}");
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes))[..8].ToUpperInvariant();
        return $"AUTO-{pillar}-{digest}";
    }

    // -------------------- helpers de parámetros SQL / historia --------------------

    private static object? ToDb(object? value) => value;

    private static object DateParam(object? value) => value switch
    {
        null => DBNull.Value,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTime dt => dt,
        _ => DBNull.Value,
    };

    private static bool TrackingEquals(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return Equals(a, b);
    }

    /// <summary>str(value) if value is not None else None (para old_value/new_value de historia).</summary>
    private static string? TrackingString(object? value) => value switch
    {
        null => null,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    // -------------------- config IA --------------------

    private bool IsAiConfigured() =>
        !string.IsNullOrWhiteSpace(config.AzureOpenAiEndpoint)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiKey)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiDeployment)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiVersion);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // ensure_ascii=False
    };
}
