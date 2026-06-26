namespace OptimizacionCostos.Api.Features.CostEngine;

/// <summary>
/// Fila de recurso a costear. Envuelve un diccionario clave/valor (datos JOIN-eados
/// de azure_resources + tabla detalle) e imita el comportamiento de <c>dict.get(...)</c>
/// de Python: claves ausentes devuelven null/default, sin lanzar excepción.
///
/// Equivale al parámetro <c>r: dict</c> que reciben las calculadoras Python
/// (ej. <c>r.get("sku_name")</c>, <c>r.get("sku_capacity")</c>).
///
/// Convención de claves: snake_case, igual que en Python (ej. "resource_id",
/// "sku_name", "sku_capacity", "location", "vm_size", "os_type", "power_state",
/// "disk_sku", "disk_size_gb", "is_linux", "is_orphan", "is_serverless",
/// "elastic_pool_name", "properties_json", "vcore_count", "storage_size_gb", etc.).
/// </summary>
public sealed class ResourceRow
{
    private readonly IReadOnlyDictionary<string, object?> _data;

    public ResourceRow(IReadOnlyDictionary<string, object?> data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
    }

    /// <summary>Diccionario subyacente (solo lectura), por si una calculadora necesita una clave no tipada.</summary>
    public IReadOnlyDictionary<string, object?> Raw => _data;

    /// <summary>True si la clave existe (aunque su valor sea null).</summary>
    public bool ContainsKey(string key) => _data.ContainsKey(key);

    /// <summary>
    /// Id del recurso, tomado de la clave "resource_id". Las calculadoras Python
    /// hacen <c>r["resource_id"]</c> (acceso directo, asume que existe).
    /// </summary>
    public int ResourceId => GetInt("resource_id")
        ?? throw new KeyNotFoundException("ResourceRow no tiene clave 'resource_id'.");

    /// <summary>
    /// Cadena en la clave dada, o null si la clave no existe o el valor es null.
    /// Imita <c>r.get(key)</c> con valor str.
    /// </summary>
    public string? GetString(string key)
    {
        if (!_data.TryGetValue(key, out var v) || v is null) return null;
        return v as string ?? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Entero en la clave dada (int?), o null si la clave no existe / es null / no parseable.
    /// Acepta int, long, double, bool y string numérico. Imita <c>r.get(key)</c> con valor int.
    /// </summary>
    public int? GetInt(string key)
    {
        if (!_data.TryGetValue(key, out var v) || v is null) return null;
        switch (v)
        {
            case int i: return i;
            case long l: return (int)l;
            case short s: return s;
            case byte b: return b;
            case double d: return (int)d;
            case float f: return (int)f;
            case decimal m: return (int)m;
            case bool bo: return bo ? 1 : 0;
            case string str when int.TryParse(str, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed): return parsed;
            case string str when double.TryParse(str, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var pd): return (int)pd;
            default: return null;
        }
    }

    /// <summary>
    /// Double en la clave dada (double?), o null si la clave no existe / es null / no parseable.
    /// Acepta double, float, int, long, decimal y string numérico. Imita <c>r.get(key)</c> con valor float.
    /// </summary>
    public double? GetDouble(string key)
    {
        if (!_data.TryGetValue(key, out var v) || v is null) return null;
        switch (v)
        {
            case double d: return d;
            case float f: return f;
            case int i: return i;
            case long l: return l;
            case short s: return s;
            case byte b: return b;
            case decimal m: return (double)m;
            case bool bo: return bo ? 1.0 : 0.0;
            case string str when double.TryParse(str, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed): return parsed;
            default: return null;
        }
    }

    /// <summary>
    /// Booleano "truthy" estilo Python <c>bool(r.get(key))</c>: false si la clave no existe,
    /// es null, es 0/0.0, cadena vacía o "false"/"0"/"no". true en cualquier otro caso con contenido.
    /// </summary>
    public bool GetBool(string key)
    {
        if (!_data.TryGetValue(key, out var v) || v is null) return false;
        switch (v)
        {
            case bool b: return b;
            case int i: return i != 0;
            case long l: return l != 0;
            case short s: return s != 0;
            case byte by: return by != 0;
            case double d: return d != 0.0;
            case float f: return f != 0.0f;
            case decimal m: return m != 0m;
            case string str:
                var t = str.Trim().ToLowerInvariant();
                if (t.Length == 0) return false;
                return t is not ("false" or "0" or "no");
            default:
                return true; // objeto no nulo no vacío => truthy
        }
    }
}
