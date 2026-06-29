using System.Globalization;
using System.Text.Json.Nodes;

namespace OptimizacionCostos.Api.Features.Inventory;

/// <summary>
/// Lector tipado de una fila de Azure Resource Graph (un <see cref="JsonObject"/>). Replica el
/// acceso flexible de Python <c>row.get("campo")</c>: los meters/columnas de la KQL pueden venir
/// como número, string o bool, así que cada accesor tolera el tipo subyacente.
/// </summary>
public readonly struct RgRow(JsonNode? node)
{
    private readonly JsonObject _obj = node as JsonObject ?? new JsonObject();

    private JsonNode? Get(string key) => _obj.TryGetPropertyValue(key, out var v) ? v : null;

    /// <summary>Nodo crudo (p.ej. "properties", objeto anidado).</summary>
    public JsonNode? Node(string key) => Get(key);

    public string? Str(string key)
    {
        var n = Get(key);
        if (n is null) return null;
        return n.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => n.GetValue<string>(),
            System.Text.Json.JsonValueKind.Null => null,
            _ => n.ToJsonString().Trim('"'),
        };
    }

    public int? Int(string key)
    {
        var n = Get(key);
        if (n is null) return null;
        if (n.GetValueKind() == System.Text.Json.JsonValueKind.Number && n.AsValue().TryGetValue(out int i)) return i;
        var s = Str(key);
        return int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    public long? Long(string key)
    {
        var n = Get(key);
        if (n is null) return null;
        if (n.GetValueKind() == System.Text.Json.JsonValueKind.Number && n.AsValue().TryGetValue(out long l)) return l;
        var s = Str(key);
        return long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    public double? Dbl(string key)
    {
        var n = Get(key);
        if (n is null) return null;
        if (n.GetValueKind() == System.Text.Json.JsonValueKind.Number && n.AsValue().TryGetValue(out double d)) return d;
        var s = Str(key);
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    /// <summary>Booleano "truthy" estilo Python: true/1/"true"/"1" → true; ausente → null.</summary>
    public bool? BoolN(string key)
    {
        var n = Get(key);
        if (n is null) return null;
        return n.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Number => (n.AsValue().TryGetValue(out double d) ? d : 0) != 0,
            System.Text.Json.JsonValueKind.String => Str(key)?.Trim().ToLowerInvariant() is "true" or "1",
            _ => null,
        };
    }

    /// <summary>Como BoolN pero ausente → false (para los <c>1 if x else 0</c> sin None).</summary>
    public bool BoolFalse(string key) => BoolN(key) ?? false;

    /// <summary>Lista de strings de un array JSON (p.ej. make_set de la KQL de discovery).</summary>
    public IReadOnlyList<string> StrList(string key)
    {
        if (Get(key) is JsonArray arr)
            return arr.Where(x => x is not null).Select(x => x!.ToString()).ToList();
        return [];
    }
}
