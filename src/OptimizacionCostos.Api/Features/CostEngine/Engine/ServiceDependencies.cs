namespace OptimizacionCostos.Api.Features.CostEngine.Engine;

/// <summary>
/// Dependencias y servicios internos del catálogo. Port 1:1 de service_catalog.py
/// (INTERNAL_SERVICE_KEYS, SERVICE_DEPENDENCIES, expand_service_keys).
/// </summary>
public static class ServiceDependencies
{
    /// <summary>Servicios que solo aportan metadata (no generan cost_results propios).</summary>
    public static readonly IReadOnlySet<string> InternalServiceKeys =
        new HashSet<string>(StringComparer.Ordinal) { "sql_vm" };

    /// <summary>service_key → dependencias que deben incluirse al calcularlo.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> Dependencies =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["vms"] = ["sql_vm"],
        };

    /// <summary>
    /// Expande las claves pedidas agregando sus dependencias (preservando orden, sin duplicados).
    /// Devuelve la misma referencia (null/empty) si no hay claves, igual que el Python.
    /// </summary>
    public static IReadOnlyList<string>? ExpandServiceKeys(IReadOnlyList<string>? serviceKeys)
    {
        if (serviceKeys is null || serviceKeys.Count == 0)
        {
            return serviceKeys;
        }

        var expanded = new List<string>();
        foreach (var key in serviceKeys)
        {
            if (!expanded.Contains(key))
            {
                expanded.Add(key);
            }
            if (Dependencies.TryGetValue(key, out var deps))
            {
                foreach (var dep in deps)
                {
                    if (!expanded.Contains(dep))
                    {
                        expanded.Add(dep);
                    }
                }
            }
        }
        return expanded;
    }
}
