using System.Text.Json;
using System.Text.RegularExpressions;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Helpers internos para parsear respuestas IA del módulo WAF (port de _parse_json de
/// dedup.py / ai_curator.py). Tolera markdown (```json ... ```) y extrae el primer objeto
/// {...} si el texto completo no es JSON válido. El IChatCompletionClient existente ya entrega
/// el "content" del assistant extraído, por lo que NO se necesita el _extract_text del Python
/// (que cubría el shape crudo de /openai/responses).
/// </summary>
internal static partial class WafAiJson
{
    // ^```(?:json)? | ```$   (IGNORECASE) — port exacto del strip de backticks del Python.
    [GeneratedRegex(@"^```(?:json)?|```$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Fences();

    // \{.*\}  (DOTALL) — primer objeto JSON dentro del texto.
    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)]
    private static partial Regex JsonObject();

    /// <summary>
    /// Port de dedup.py::_parse_json: limpia backticks, busca el primer {...} (DOTALL) y parsea
    /// ESE substring si existe; si no, parsea el texto limpio directo. Devuelve el JsonElement raíz
    /// (el llamador lo lee con TryGetProperty). Lanza JsonException si no hay JSON válido.
    /// </summary>
    public static JsonDocument ParseDedup(string text)
    {
        var clean = Fences().Replace((text ?? "").Trim(), "").Trim();
        var match = JsonObject().Match(clean);
        var json = match.Success ? match.Value : clean;
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Port de ai_curator.py::_parse_json: intenta parsear el texto limpio; si falla, busca {...}
    /// (DOTALL) y parsea ese match; si tampoco hay match, lanza (equivale al ValueError del Python).
    /// </summary>
    public static JsonDocument ParseCurator(string text)
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

    /// <summary>Lee una propiedad string (o null) tolerando tipos no-string.</summary>
    public static string? GetString(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => p.GetRawText(),
        };
    }

    /// <summary>Lee un bool tolerante (true/false, "true", 1). Default false (bool(...) del Python).</summary>
    public static bool GetBoolTruthy(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return false;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => p.TryGetDouble(out var d) && d != 0,
            JsonValueKind.String => p.GetString() is { Length: > 0 } s
                && s.Trim().ToLowerInvariant() is "true" or "1" or "yes",
            _ => false,
        };
    }

    /// <summary>int(parsed.get(name) or 0): número, string numérica o 0. Tolerante.</summary>
    public static int GetIntOrZero(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return 0;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetInt32(out var i) ? i : (p.TryGetDouble(out var d) ? (int)d : 0),
            JsonValueKind.String => int.TryParse(p.GetString(), out var s) ? s
                : (double.TryParse(p.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var sd) ? (int)sd : 0),
            _ => 0,
        };
    }

    /// <summary>float(parsed.get(name) or 0) tolerante; null si ausente para distinguir el default.</summary>
    public static double? GetDoubleOrNull(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(p.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : null,
            _ => null,
        };
    }
}
