using System.Text.RegularExpressions;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Features.Boletin;

public sealed record RuntimeTarget(string Family, string Version);

public sealed record SiteRuntime(string SubscriptionId, string SiteId, string SiteName, string Runtime);

/// <summary>Detectores de inventario: derivan recursos impactados para avisos de Service Health
/// que Microsoft publica sin detalle (verificado: 0/13 retiros con impactedresources).
/// Las filas emitidas llevan Derived=true — la UI las marca "Inventario" (inferido por BIT).</summary>
public static partial class BoletinDetectors
{
    // "Node.js 20", "Node 20", "Python-2.7", "PHP 8.1", ".NET 8", "PowerShell- 7.1".
    // Tras la versión capturada, una lista "; 3.8" / "y 7.2" / "& 3.8" extiende la MISMA familia.
    // Nota: sustituye \b con (?<!\w) para manejar ".NET" (punto) correctamente.
    [GeneratedRegex(@"(?i)(?<!\w)(node(?:\.js)?|python|php|powershell|java|\.net)[\s\-]*v?(\d+(?:\.\d+)?)((?:\s*[;,&]\s*(?:and\s+)?\d+(?:\.\d+)?)*)")]
    private static partial Regex RuntimeRegex();

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex VersionRegex();

    public static IReadOnlyList<RuntimeTarget> MatchAnnouncement(string title)
    {
        var targets = new List<RuntimeTarget>();
        foreach (Match m in RuntimeRegex().Matches(title ?? ""))
        {
            var family = NormalizeFamily(m.Groups[1].Value);
            if (family is null) continue;
            targets.Add(new RuntimeTarget(family, m.Groups[2].Value));
            foreach (Match extra in VersionRegex().Matches(m.Groups[3].Value))
                targets.Add(new RuntimeTarget(family, extra.Value));
        }
        return targets.Distinct().ToList();
    }

    /// <summary>Compara un target contra un token de runtime ("NODE|20-lts", "DOTNETCORE|8.0").</summary>
    public static bool RuntimeMatches(RuntimeTarget target, string runtime)
    {
        var parts = (runtime ?? "").Split('|', 2);
        if (parts.Length < 2) return false;
        var family = NormalizeFamily(parts[0]);
        if (family != target.Family) return false;
        var version = parts[1].Trim().Replace("-lts", "", StringComparison.OrdinalIgnoreCase).TrimStart('~', 'v', 'V');
        // "20" matchea "20" y "20.x"; "3.9" solo "3.9" (y "3.9.x").
        return version.Equals(target.Version, StringComparison.OrdinalIgnoreCase)
            || version.StartsWith(target.Version + ".", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? NormalizeFamily(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "node" or "node.js" => "node",
        "python" => "python",
        "php" => "php",
        "powershell" => "powershell",
        "java" => "java",
        ".net" or "dotnet" or "dotnetcore" => "dotnet",
        _ => null,
    };

    public static SiteRuntime? FromLinuxSiteRow(RgRow row)
    {
        var sub = (row.Str("subscriptionId") ?? "").Trim();
        var id = (row.Str("siteId") ?? "").Trim();
        var runtime = (row.Str("runtime") ?? "").Trim();
        if (sub.Length == 0 || id.Length == 0 || runtime.Length == 0) return null;
        return new SiteRuntime(sub, id, (row.Str("name") ?? "").Trim(), runtime);
    }

    /// <summary>Filas derivadas para UN aviso: sitios de la MISMA suscripción cuyo runtime matchea
    /// algún target, excluyendo recursos ya presentes (p. ej. del enriquecimiento de Microsoft,
    /// que siempre gana sobre lo inferido).</summary>
    public static IReadOnlyList<RetirementRow> BuildDerivedRows(
        RetirementRow announcement, IReadOnlyList<RuntimeTarget> targets,
        IReadOnlyList<SiteRuntime> sites, ISet<string> existingResourceIdsLower)
    {
        if (targets.Count == 0) return [];
        return sites
            .Where(s => string.Equals(s.SubscriptionId, announcement.SubscriptionId, StringComparison.OrdinalIgnoreCase))
            .Where(s => !existingResourceIdsLower.Contains(s.SiteId.ToLowerInvariant()))
            .Where(s => targets.Any(t => RuntimeMatches(t, s.Runtime)))
            .Select(s => announcement with
            {
                AzureResourceId = s.SiteId,
                ResourceName = s.SiteName,
                ResourceType = "Microsoft.Web/sites",
                Derived = true,
            })
            .ToList();
    }
}
