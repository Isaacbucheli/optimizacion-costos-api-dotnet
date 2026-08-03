using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Features.Boletin;

/// <summary>Recurso del inventario candidato a fin de soporte, ya normalizado para el matching.</summary>
public sealed record EolResource(string SubscriptionId, string ResourceId, string ResourceName,
    string ResourceType, string Haystack, string MatchField);

/// <summary>Cruce del inventario (SO por VM, imagen SQL) contra el catálogo de lifecycle.
/// Puro y testeable. Las filas emitidas son source='eol', contenido ES-nativo (autoría BIT,
/// sin traducción IA) y Derived=false (la pestaña propia comunica el origen).</summary>
public static class BoletinEol
{
    public static EolResource? FromVmOsRow(RgRow row)
    {
        var sub = (row.Str("subscriptionId") ?? "").Trim();
        var id = (row.Str("vmId") ?? "").Trim();
        var osName = (row.Str("osName") ?? "").Trim();
        if (sub.Length == 0 || id.Length == 0 || osName.Length == 0) return null;
        var osVersion = (row.Str("osVersion") ?? "").Trim();
        var haystack = (osName + " " + osVersion).Trim().ToLowerInvariant();
        return new EolResource(sub, id, (row.Str("name") ?? "").Trim(),
            "Microsoft.Compute/virtualMachines", haystack, "os_name");
    }

    public static EolResource? FromSqlVmRow(RgRow row)
    {
        var sub = (row.Str("subscriptionId") ?? "").Trim();
        var id = (row.Str("sqlVmId") ?? "").Trim();
        var offer = (row.Str("sqlImageOffer") ?? "").Trim();
        if (sub.Length == 0 || id.Length == 0 || offer.Length == 0) return null;
        return new EolResource(sub, id, (row.Str("name") ?? "").Trim(),
            "Microsoft.SqlVirtualMachine/sqlVirtualMachines", offer.ToLowerInvariant(), "sql_image_offer");
    }

    /// <summary>Por (recurso, match_field) gana el patrón MÁS LARGO que matchee
    /// (desambigua "windows server 2012" vs "windows server 2012 r2").
    /// El haystack ya viene en minúsculas de los parsers y match_pattern se normaliza a minúsculas
    /// en el CRUD y el seed — por eso StringComparison.Ordinal basta.</summary>
    public static IReadOnlyList<RetirementRow> MatchResources(
        IReadOnlyList<LifecycleEntry> entries, IReadOnlyList<EolResource> resources)
    {
        var rows = new List<RetirementRow>();
        foreach (var res in resources)
        {
            var best = entries
                .Where(e => e.IsActive && e.MatchField == res.MatchField
                            && res.Haystack.Contains(e.MatchPattern, StringComparison.Ordinal))
                .OrderByDescending(e => e.MatchPattern.Length)
                .FirstOrDefault();
            if (best is null) continue;
            rows.Add(new RetirementRow(
                Source: RetirementRow.SourceEol,
                AnnouncementKey: best.Clave,
                SubscriptionId: res.SubscriptionId,
                AzureResourceId: res.ResourceId,
                ResourceName: res.ResourceName,
                ResourceType: res.ResourceType,
                RetiringFeature: best.Producto,
                RetirementDate: best.EndOfSupport,
                Title: $"{best.Producto} — fin de soporte",
                Summary: null,
                RecommendedAction: best.Recomendacion,
                LearnMoreUrl: best.LearnMoreUrl));
        }
        return rows;
    }
}
