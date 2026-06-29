using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Port FIEL de app/reports/ai_narrative.py. Reusa el IChatCompletionClient existente (no construye
/// un HttpClient nuevo para OpenAI) + AppConfig (AzureOpenAi*). Replica el PROMPT EXACTO, el parseo
/// markdown/JSON (_parse_json) y la normalizacion (_normalize / _exec_normalize). Si Azure OpenAI no
/// esta configurado o el modelo no responde, devuelve null (campo narrative=None en Python).
/// NO loguea prompts ni claves.
/// </summary>
public sealed partial class ReportNarrativeService(
    IChatCompletionClient chat, AppConfig config, ILogger<ReportNarrativeService> logger)
    : IReportNarrativeService
{
    // TEXT_FIELDS de ai_narrative.py (campo -> limite).
    private static readonly (string Field, int Limit)[] TextFields =
    [
        ("resumen_ejecutivo", 2500),
        ("performance_comentario", 2000),
        ("backups_comentario", 1500),
        ("disponibilidad_comentario", 1200),
    ];

    // LIST_FIELDS de ai_narrative.py (campo -> max items).
    private static readonly (string Field, int MaxItems)[] ListFields =
    [
        ("hallazgos", 6),
        ("conclusiones", 6),
        ("recomendaciones", 6),
    ];

    // EXEC_TEXT_FIELDS de ai_narrative.py.
    private static readonly (string Field, int Limit)[] ExecTextFields =
    [
        ("titular", 160),
        ("sintesis", 900),
    ];

    // EXEC_DOMAINS de ai_narrative.py (orden EXACTO).
    private static readonly string[] ExecDomains =
    [
        "disponibilidad", "vms", "paas", "backup",
        "conectividad", "rbac", "alertas",
    ];

    // json.dumps(context, ensure_ascii=False): UTF-8 sin escapar no-ASCII, separadores compactos.
    private static readonly JsonSerializerOptions DumpsOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    // ---------- Narrativa del informe detallado ----------

    public async Task<ReportNarrative?> GenerateNarrativeAsync(JsonNode context, CancellationToken ct = default)
    {
        var data = await CallModelAsync(BuildPrompt(context), 3000, ct);
        return data is null ? null : Normalize(data.Value);
    }

    // ai_narrative.py::_prompt — texto EXACTO. Solo se interpola el JSON de los datos del mes.
    private static string BuildPrompt(JsonNode context)
    {
        var datos = Dumps(context);
        return $$"""
Eres un consultor senior Azure de Business IT (Cloud Data Center). Redacta la
narrativa del Informe de Gestion mensual de Servicios Administrados Azure de un
cliente, a partir de los datos agregados que se entregan abajo (inventario,
estado de recursos, performance, respaldos, disponibilidad y recomendaciones).

Reglas:
- Devuelve unicamente JSON valido, sin markdown ni caracteres decorativos.
- Espanol corporativo claro, dirigido al cliente. No inventes cifras: usa solo
  las que vienen en los datos.
- PROHIBIDO mencionar costos, precios, montos, dolares, ahorros economicos,
  reservas de instancias o cualquier cifra economica. El informe es de gestion
  operativa, no financiero.
- Si una seccion no tiene datos (lista vacia o null), devuelve "" o [] en su campo.
- Destaca VMs con CPU o RAM promedio sobre 80% o maximos sostenidos en 100%,
  y VMs encendidas con bajo uso (CPU < 5%) como oportunidad de revision operativa.
- En respaldos comenta cobertura (items protegidos vs servidores) y fallos si los hay.
- En disponibilidad comenta los eventos de Azure del periodo (si existen) y su impacto.
- "hallazgos", "conclusiones" y "recomendaciones" son listas de maximo 6 strings,
  cada uno puntual y accionable.

Datos del mes:
{{datos}}

JSON requerido:
{
  "resumen_ejecutivo": "",
  "hallazgos": [],
  "performance_comentario": "",
  "backups_comentario": "",
  "disponibilidad_comentario": "",
  "conclusiones": [],
  "recomendaciones": []
}
""".Trim();
    }

    // ai_narrative.py::_normalize
    private static ReportNarrative Normalize(JsonElement data)
    {
        var texts = new Dictionary<string, string>();
        foreach (var (field, limit) in TextFields)
            texts[field] = ReportAiClean.Clean(GetMember(data, field), limit);

        var lists = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (field, maxItems) in ListFields)
        {
            var values = GetMember(data, field);
            var cleaned = new List<string>();
            if (values is { ValueKind: JsonValueKind.Array })
            {
                // values[:max_items] -> _clean(item, 500) -> [item for item if item]
                var count = 0;
                foreach (var item in values.Value.EnumerateArray())
                {
                    if (count >= maxItems) break;
                    count++;
                    var c = ReportAiClean.Clean(item, 500);
                    if (!string.IsNullOrEmpty(c)) cleaned.Add(c);
                }
            }
            lists[field] = cleaned;
        }

        return new ReportNarrative(
            ResumenEjecutivo: texts["resumen_ejecutivo"],
            Hallazgos: lists["hallazgos"],
            PerformanceComentario: texts["performance_comentario"],
            BackupsComentario: texts["backups_comentario"],
            DisponibilidadComentario: texts["disponibilidad_comentario"],
            Conclusiones: lists["conclusiones"],
            Recomendaciones: lists["recomendaciones"]);
    }

    // ---------- Narrativa gerencial (dashboard ejecutivo) ----------

    public async Task<ReportExecutiveNarrative?> GenerateExecutiveNarrativeAsync(JsonNode context, CancellationToken ct = default)
    {
        var data = await CallModelAsync(BuildExecPrompt(context), 2500, ct);
        return data is null ? null : ExecNormalize(data.Value);
    }

    // ai_narrative.py::_exec_prompt — texto EXACTO.
    private static string BuildExecPrompt(JsonNode context)
    {
        var datos = Dumps(context);
        return $$"""
Eres un consultor senior Azure de Business IT (Cloud Data Center). Redacta los
textos del Dashboard Gerencial mensual de un cliente, dirigido a gerencia /
comite ejecutivo, a partir de los datos y el semaforo por dominio que se
entregan abajo. El semaforo ya fue calculado con reglas fijas: NO lo
contradigas ni lo recalcules.

Reglas:
- Devuelve unicamente JSON valido, sin markdown ni caracteres decorativos.
- Espanol corporativo claro y directo, para presentar al cliente.
- No inventes cifras ni recursos: usa solo los numeros que vienen en los datos.
- PROHIBIDO mencionar costos, precios, montos, dolares, ahorros economicos,
  reservas de instancias o cualquier cifra economica.
- "titular": una frase corta (maximo 12 palabras) que resuma el estado del mes,
  coherente con el estado general del semaforo.
- "sintesis": 2-3 frases para el resumen del comite: que funciono, cual es el
  riesgo principal y que se debe atender primero.
- "lectura_dominios": para CADA dominio presente en el semaforo, 1-2 frases de
  lectura gerencial (que significa el estado para la operacion y que sigue).
  Usa las claves del campo 'dominio' tal cual.

Datos del mes:
{{datos}}

JSON requerido:
{
  "titular": "",
  "sintesis": "",
  "lectura_dominios": {}
}
""".Trim();
    }

    // ai_narrative.py::_exec_normalize
    private static ReportExecutiveNarrative ExecNormalize(JsonElement data)
    {
        var texts = new Dictionary<string, string>();
        foreach (var (field, limit) in ExecTextFields)
            texts[field] = ReportAiClean.Clean(GetMember(data, field), limit);

        var domains = new Dictionary<string, string>();
        var rawDomains = GetMember(data, "lectura_dominios");
        if (rawDomains is { ValueKind: JsonValueKind.Object })
        {
            foreach (var key in ExecDomains)
            {
                var text = ReportAiClean.Clean(GetMember(rawDomains.Value, key), 600);
                if (!string.IsNullOrEmpty(text)) domains[key] = text;
            }
        }

        return new ReportExecutiveNarrative(
            Titular: texts["titular"],
            Sintesis: texts["sintesis"],
            LecturaDominios: domains);
    }

    // ---------- Llamada al modelo ----------

    /// <summary>
    /// Port de ai_narrative.py::_call_model. Llama Azure OpenAI vía el IChatCompletionClient existente
    /// y devuelve el JSON parseado, o null si no esta configurado / no respondio tras reintentos.
    /// El IChatCompletionClient esconde la capa HTTP (no expone 429/503/Retry-After), asi que se
    /// reintenta ante CUALQUIER fallo con backoff 2^(attempt+1) cap 30s (paridad de la escalera del
    /// Python). Nunca lanza: ante fallo final loguea y devuelve null (igual que el return None).
    /// max_output_tokens del Python no se reenvia: el IChatCompletionClient fija sus propios tokens.
    /// </summary>
    private async Task<JsonElement?> CallModelAsync(string prompt, int maxOutputTokens, CancellationToken ct)
    {
        if (!IsConfigured())
            return null;

        string? raw = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // El Python manda max_output_tokens (3000 narrativa / 2500 ejecutiva): la narrativa
                // del informe es larga (resumen hasta 2500 chars) y 500 tokens la truncarian.
                raw = await Task.Run(() => chat.Complete("", prompt, maxOutputTokens), ct);
                if (string.IsNullOrWhiteSpace(raw))
                    throw new InvalidOperationException("respuesta IA vacia");
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= 3)
                {
                    // Python: logger.warning("Narrativa IA fallo/sin conexion ...") -> return None
                    logger.LogWarning("Narrativa IA fallo tras reintentos: {Error}", ex.Message);
                    return null;
                }
                var wait = Math.Min(Math.Pow(2, attempt + 1), 30.0);
                try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); }
                catch (OperationCanceledException) { throw; }
            }
        }
        if (raw is null)
            return null;

        try
        {
            // _parse_json(_extract_text(...)): el IChatCompletionClient ya entrega el "content"
            // del assistant, por lo que NO se necesita _extract_text (cubria el shape de /responses).
            using var doc = ParseJson(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Narrativa IA con respuesta invalida: {Error}", ex.Message);
            return null;
        }
        catch (FormatException ex)
        {
            logger.LogWarning("Narrativa IA con respuesta invalida: {Error}", ex.Message);
            return null;
        }
    }

    // azure_openai_config_status()["configured"] (via ai_curator) — endpoint+key+deployment+api_version.
    private bool IsConfigured()
    {
        var endpoint = (config.AzureOpenAiEndpoint ?? "").Trim();
        var key = (config.AzureOpenAiApiKey ?? "").Trim();
        var deployment = (config.AzureOpenAiDeployment ?? "").Trim();
        var apiVersion = string.IsNullOrWhiteSpace(config.AzureOpenAiApiVersion)
            ? "2025-04-01-preview" : config.AzureOpenAiApiVersion.Trim();
        return endpoint.Length > 0 && key.Length > 0 && deployment.Length > 0 && apiVersion.Length > 0;
    }

    // ---------- Helpers de parseo / serializacion ----------

    // ai_curator.py::_parse_json: limpia backticks, intenta parsear directo; si falla, busca {...}
    // (DOTALL) y parsea ese match; si no hay match, lanza (equivale al ValueError del Python).
    private static JsonDocument ParseJson(string text)
    {
        var clean = Fences().Replace((text ?? "").Trim(), "").Trim();
        try
        {
            return JsonDocument.Parse(clean);
        }
        catch (JsonException)
        {
            var match = JsonObject().Match(clean);
            if (!match.Success)
            {
                var preview = clean.Length > 500 ? clean[..500] : clean;
                throw new FormatException($"La respuesta IA no contiene JSON valido: {preview}");
            }
            return JsonDocument.Parse(match.Value);
        }
    }

    // json.dumps(context, ensure_ascii=False)
    private static string Dumps(JsonNode context) =>
        context.ToJsonString(DumpsOptions);

    // data.get(field): null si ausente o JSON null.
    private static JsonElement? GetMember(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.Null ? null : p;
    }

    // ^```(?:json)? | ```$  (IGNORECASE) — port EXACTO del strip de backticks de ai_curator.py::
    // _parse_json (re.sub(..., flags=re.IGNORECASE)). El Python NO usa MULTILINE aqui: ^/$ anclan a
    // todo el texto, no por linea. (El WafAiJson.ParseCurator si agrego Multiline; aqui se respeta el
    // comportamiento del Python para la paridad 1:1 que pide el port del informe.)
    [GeneratedRegex(@"^```(?:json)?|```$", RegexOptions.IgnoreCase)]
    private static partial Regex Fences();

    // \{.*\}  (DOTALL) — primer objeto JSON dentro del texto.
    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)]
    private static partial Regex JsonObject();
}

/// <summary>
/// Port FIEL de ai_curator.py::_clean (reusado por ai_narrative.py via import). NFKC + remocion de
/// controles/zero-width, markdown, emojis y secuencias decorativas, colapso de espacios y strip de
/// " -". Acepta str|null|JsonElement (str(value or "") del Python: numeros/bool/listas -> texto).
/// </summary>
internal static partial class ReportAiClean
{
    // Python: [ --​-‍﻿�] -> " "
    [GeneratedRegex("[ --​-‍﻿�]")]
    private static partial Regex ControlChars();

    // Python: ```(?:json|text|markdown)?  (IGNORECASE) -> " "
    [GeneratedRegex("```(?:json|text|markdown)?", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownFences();

    // Python: [\U0001F300-\U0001FAFF\U00002700-\U000027BF] -> ""  (emoji plano supl. + dingbats BMP)
    // Los 3 tramos de subrogados cubren EXACTO U+1F300-U+1FAFF (no U+1F000-U+1F2FF ni U+1FB00+).
    [GeneratedRegex("[✀-➿]|\uD83C[\uDF00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDC00-\uDEFF]")]
    private static partial Regex Emojis();

    // Python: ([#*_~=\-])\1{2,} -> " "  (3+ repeticiones del mismo char decorativo)
    [GeneratedRegex(@"([#*_~=\-])\1{2,}")]
    private static partial Regex RepeatDecor();

    public static string Clean(JsonElement? value, int limit)
    {
        var text = (Stringify(value) ?? "").Normalize(NormalizationForm.FormKC); // Python usa NFKC
        text = ControlChars().Replace(text, " ");
        text = MarkdownFences().Replace(text, " ");
        text = Emojis().Replace(text, "");
        text = RepeatDecor().Replace(text, " ");
        // " ".join(text.split()) — colapsa cualquier whitespace
        text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // .strip(" -") — quita espacios y guiones de los extremos
        text = text.Trim(' ', '-');
        return text.Length > limit ? text[..limit] : text;
    }

    // str(value or "") del Python: los valores 'falsy' (None/""/0/0.0/False/[]/{}) colapsan a "".
    private static string? Stringify(JsonElement? value)
    {
        if (value is not { } el) return "";
        return el.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            JsonValueKind.String => el.GetString(),
            JsonValueKind.True => "True",                 // str(True)
            JsonValueKind.False => "",                    // False es falsy -> "" (value or "")
            JsonValueKind.Number => el.TryGetDouble(out var d) && d == 0 ? "" : el.GetRawText(), // 0/0.0 falsy
            JsonValueKind.Array => el.GetArrayLength() > 0 ? el.GetRawText() : "",     // [] falsy -> ""
            JsonValueKind.Object => el.EnumerateObject().Any() ? el.GetRawText() : "", // {} falsy -> ""
            _ => el.GetRawText(),
        };
    }
}
