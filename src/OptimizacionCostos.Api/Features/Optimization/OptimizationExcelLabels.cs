using System.Text.Json;

namespace OptimizacionCostos.Api.Features.Optimization;

/// <summary>
/// Metadatos de presentación del export Excel de Oportunidades de Optimización.
/// Espejo del front React (innovacion-CDC/src/lib/optimization.ts CHECK_META): mismo
/// mapa check_id → grupo y mismos títulos en español. Si se agrega un check nuevo,
/// actualizar AMBOS lados.
/// </summary>
public static class OptimizationExcelLabels
{
    public static readonly IReadOnlyList<string> GroupOrder =
        ["Azure Hybrid Benefit", "Compute", "Storage", "Networking", "Otros"];

    private static readonly Dictionary<string, (string Title, string Group)> CheckMeta = new(StringComparer.Ordinal)
    {
        ["vms_without_ahb"] = ("Azure Hybrid Benefit no aplicado", "Azure Hybrid Benefit"),
        ["underutilized_vms"] = ("VMs subutilizadas (right-sizing)", "Compute"),
        ["stopped_not_deallocated_vms"] = ("VMs detenidas no desasignadas", "Compute"),
        ["long_deallocated_vms"] = ("VMs desasignadas (posibles olvidadas)", "Compute"),
        ["empty_app_service_plans"] = ("App Service Plans sin aplicaciones", "Compute"),
        ["vms_without_ha"] = ("VMs sin alta disponibilidad", "Compute"),
        ["orphaned_disks"] = ("Discos administrados no conectados", "Storage"),
        ["old_snapshots"] = ("Snapshots antiguos (>90 días)", "Storage"),
        ["storage_no_retention"] = ("Storage sin política de retención", "Storage"),
        ["orphaned_public_ips"] = ("Public IPs sin asociar", "Networking"),
        ["orphaned_nics"] = ("Interfaces de red huérfanas", "Networking"),
        ["empty_subnets"] = ("Subnets vacías", "Networking"),
        ["lb_appgw_no_backend"] = ("Balanceadores / App Gateways sin backend", "Networking"),
        ["basic_load_balancers"] = ("Load Balancers Basic (en retiro)", "Networking"),
    };

    public static string CheckTitle(string checkId) =>
        CheckMeta.TryGetValue(checkId, out var m) ? m.Title : checkId;

    public static string CheckGroup(string checkId) =>
        CheckMeta.TryGetValue(checkId, out var m) ? m.Group : "Otros";

    public static string SeverityLabel(string? severity) => severity switch
    {
        "high" => "Alta", "medium" => "Media", "low" => "Baja", _ => severity ?? "",
    };

    public static string StateLabel(string? state) => state switch
    {
        "abierto" => "Abierto", "en_progreso" => "En progreso",
        "resuelto" => "Resuelto", "ignorado" => "Ignorado", _ => state ?? "",
    };

    /// <summary>"/subscriptions/{s}/resourceGroups/{rg}/..." → rg; malformado → "".</summary>
    public static string ParseResourceGroup(string? azureResourceId)
    {
        if (string.IsNullOrEmpty(azureResourceId)) return "";
        var parts = azureResourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
            if (string.Equals(parts[i], "resourceGroups", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        return "";
    }

    /// <summary>
    /// Texto compacto de detalle desde `details` (JsonElement al venir de la BD; también acepta
    /// diccionario para tests). Port de detailText del front (FindingBits.tsx).
    /// </summary>
    public static string DetailText(object? details)
    {
        var parts = new List<string>();
        var sku = Get(details, "sku"); if (sku != "") parts.Add(sku);
        var vmSize = Get(details, "vmSize"); if (vmSize != "") parts.Add(vmSize);
        var size = Get(details, "diskSizeGB"); if (size != "") parts.Add($"{size} GB");
        var power = Get(details, "powerState"); if (power != "") parts.Add(power);
        var lic = Get(details, "licenseType"); if (lic != "") parts.Add(lic);
        return parts.Count > 0 ? string.Join(" · ", parts) : Get(details, "note");
    }

    private static string Get(object? details, string key) => details switch
    {
        JsonElement e when e.ValueKind == JsonValueKind.Object
                           && e.TryGetProperty(key, out var v)
                           && v.ValueKind != JsonValueKind.Null =>
            v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString(),
        IReadOnlyDictionary<string, object?> d when d.TryGetValue(key, out var v) && v is not null =>
            v.ToString() ?? "",
        _ => "",
    };

    /// <summary>Filtra por la clave "state" de cada fila; `states` vacío → todas.</summary>
    public static List<IReadOnlyDictionary<string, object?>> FilterByStates(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> findings, IReadOnlyCollection<string> states)
    {
        if (states.Count == 0) return findings.ToList();
        var set = new HashSet<string>(states, StringComparer.Ordinal);
        return findings
            .Where(f => set.Contains((f.TryGetValue("state", out var s) ? s as string : null) ?? "abierto"))
            .ToList();
    }
}
