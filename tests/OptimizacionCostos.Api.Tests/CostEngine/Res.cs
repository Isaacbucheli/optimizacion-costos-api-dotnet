using OptimizacionCostos.Api.Features.CostEngine;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Helper para construir <see cref="ResourceRow"/> en tests desde pares clave/valor,
/// equivalente al dict literal que arman los tests Python.
///
/// <code>
///   var r = Res.Row(
///       ("resource_id", 1),
///       ("sku_name", "P1v3"),
///       ("sku_capacity", 2),
///       ("is_linux", true),
///       ("location", "eastus"));
/// </code>
///
/// Acepta cualquier valor (<c>object?</c>): int, double, bool, string, null o
/// colecciones (ej. el set "_stopped_vm_names_lower"). Las claves duplicadas toman
/// el último valor.
/// </summary>
public static class Res
{
    public static ResourceRow Row(params (string Key, object? Value)[] pairs)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            dict[key] = value;
        }
        return new ResourceRow(dict);
    }

    /// <summary>Variante que toma una lista de recursos (útil para Calculate(...)).</summary>
    public static IReadOnlyList<ResourceRow> Rows(params ResourceRow[] rows) => rows;
}
