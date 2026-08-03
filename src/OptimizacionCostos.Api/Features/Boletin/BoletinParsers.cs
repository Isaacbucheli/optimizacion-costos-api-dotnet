using System.Globalization;
using System.Text.RegularExpressions;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Features.Boletin;

/// <summary>Fila de <see cref="BoletinQueries.ServiceHealthImpactedResources"/> ya parseada: un
/// recurso concreto impactado por un aviso de Service Health, identificado por su trackingId +
/// suscripción (el cruce con el aviso a nivel de suscripción se hace en <see cref="BoletinParsers.ExpandHealthRows"/>).</summary>
public sealed record HealthImpactedResource(
    string TrackingId, string SubscriptionId, string ResourceId, string ResourceName, string ResourceType);

/// <summary>Convierte filas de las KQL del boletín a RetirementRow. Puro y testeable sin Azure.</summary>
public static class BoletinParsers
{
    private static readonly Regex TitleDateRegex = new(
        @"\b(?:(\d{1,2})\s+(January|February|March|April|May|June|July|August|September|October|November|December)|(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})),?\s+(\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Extrae la PRIMERA fecha del título EN del aviso ("10 November 2026" o "November 10, 2026").
    /// Los avisos de Service Health traen en ImpactMitigationTime el fin del ADVISORY, que puede diferir
    /// de la fecha real del retiro anunciada en el título (caso .NET 8: advisory hasta 28-nov-2027,
    /// retiro real 10-nov-2026). El título manda; ImpactMitigationTime queda como fallback.</summary>
    public static DateOnly? ExtractDateFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var m = TitleDateRegex.Match(title);
        if (!m.Success) return null;
        var dayRaw = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Value;
        var monthRaw = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        var year = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(dayRaw, CultureInfo.InvariantCulture);
        var month = DateTime.ParseExact(monthRaw, "MMMM", CultureInfo.InvariantCulture).Month;
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; } // "31 February 2026" u otra fecha imposible
    }

    public static RetirementRow? FromAdvisorRow(RgRow row)
    {
        var feature = (row.Str("retiringFeature") ?? "").Trim();
        var subId = (row.Str("subscriptionId") ?? "").Trim();
        if (feature.Length == 0 || subId.Length == 0) return null;
        var resourceId = (row.Str("resourceId") ?? "").Trim();
        return new RetirementRow(
            Source: RetirementRow.SourceAdvisor,
            AnnouncementKey: feature,
            SubscriptionId: subId,
            AzureResourceId: resourceId.Length == 0 ? null : resourceId,
            ResourceName: (row.Str("impactedValue") ?? "").Trim(),
            ResourceType: (row.Str("impactedField") ?? "").Trim(),
            RetiringFeature: feature,
            RetirementDate: ParseDate(row.Str("retirementDate")),
            Title: (row.Str("problem") is { Length: > 0 } p ? p : feature).Trim(),
            Summary: null,
            RecommendedAction: row.Str("solution"),
            LearnMoreUrl: row.Str("learnMore"));
    }

    public static RetirementRow? FromHealthRow(RgRow row)
    {
        var tracking = (row.Str("trackingId") ?? "").Trim();
        var subId = (row.Str("subscriptionId") ?? "").Trim();
        if (tracking.Length == 0 || subId.Length == 0) return null;
        var title = (row.Str("title") is { Length: > 0 } t ? t : tracking).Trim();
        return new RetirementRow(
            Source: RetirementRow.SourceServiceHealth,
            AnnouncementKey: tracking,
            SubscriptionId: subId,
            AzureResourceId: null,
            ResourceName: "",
            ResourceType: "",
            RetiringFeature: "",
            RetirementDate: ExtractDateFromTitle(title) ?? ParseDate(row.Str("impactMitigationTime")),
            Title: title,
            Summary: row.Str("summary"),
            RecommendedAction: null,
            LearnMoreUrl: null);
    }

    internal static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateOnly.FromDateTime(d) : null;
    }

    /// <summary>Parsea una fila de <see cref="BoletinQueries.ServiceHealthImpactedResources"/>.
    /// El tipo "impactedresources" no trae el trackingId como propiedad directa: se extrae del
    /// <c>id</c> completo partiendo por "/events/" (ver <see cref="ExtractTrackingIdFromId"/>).
    /// Descarta filas sin trackingId/subscriptionId/resourceId: sin esas tres claves no se puede
    /// cruzar con el aviso a nivel de suscripción (<see cref="ExpandHealthRows"/>) ni identificar
    /// el recurso. Los nombres de propiedad exactos bajo `properties` no están 100% confirmados
    /// contra Azure real (ver comentario de la KQL) — de ahí la tolerancia a que falten.</summary>
    public static HealthImpactedResource? FromHealthImpactedRow(RgRow row)
    {
        var subId = (row.Str("subscriptionId") ?? "").Trim();
        var trackingId = ExtractTrackingIdFromId(row.Str("id"));
        var resourceId = (row.Str("targetResourceId") ?? "").Trim();
        if (subId.Length == 0 || trackingId.Length == 0 || resourceId.Length == 0) return null;

        var resourceName = (row.Str("resourceName") ?? "").Trim();
        if (resourceName.Length == 0)
            // Fallback defensivo: si la propiedad de nombre no viene (o no se llama como esperamos),
            // el último segmento del resourceId suele coincidir con el nombre del recurso en Azure.
            resourceName = resourceId[(resourceId.LastIndexOf('/') + 1)..];

        return new HealthImpactedResource(
            TrackingId: trackingId, SubscriptionId: subId, ResourceId: resourceId,
            ResourceName: resourceName, ResourceType: (row.Str("targetResourceType") ?? "").Trim());
    }

    /// <summary>Extrae el trackingId de un id de fila "impactedresources" con la forma
    /// ".../events/{trackingId}/impactedResources/{n}", partiendo por "/events/" (case-insensitive,
    /// por si Azure cambia el casing de la ruta). Vacío si el id no tiene esa forma.</summary>
    internal static string ExtractTrackingIdFromId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        const string marker = "/events/";
        var idx = id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        var rest = id[(idx + marker.Length)..];
        var slash = rest.IndexOf('/');
        return (slash < 0 ? rest : rest[..slash]).Trim();
    }

    /// <summary>Cruza los avisos de Service Health (a nivel de suscripción, de
    /// <see cref="FromHealthRow"/>) con los recursos concretos que haya impactado cada uno (de
    /// <see cref="FromHealthImpactedRow"/>), por trackingId (AnnouncementKey) + SubscriptionId —
    /// deliberadamente NO solo por trackingId, porque un mismo aviso puede llegar para varias
    /// suscripciones y los recursos de una NO deben cruzarse con el aviso de otra.
    /// Por cada aviso: si hay recursos para su (trackingId, subscriptionId), emite una
    /// RetirementRow POR RECURSO (mismo Title/Summary/fecha del aviso, resource_id/name/type del
    /// recurso) EN VEZ de la fila a nivel de suscripción; si no hay ninguno, se mantiene la fila
    /// a nivel de suscripción tal como llega (cobertura parcial de Microsoft).</summary>
    public static IReadOnlyList<RetirementRow> ExpandHealthRows(
        IReadOnlyList<RetirementRow> healthRows, IReadOnlyList<HealthImpactedResource> impactedResources)
    {
        var bySub = impactedResources
            .GroupBy(r => Key(r.TrackingId, r.SubscriptionId))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<HealthImpactedResource>)g.ToList());

        var result = new List<RetirementRow>();
        foreach (var row in healthRows)
        {
            if (bySub.TryGetValue(Key(row.AnnouncementKey, row.SubscriptionId), out var resources) && resources.Count > 0)
            {
                foreach (var res in resources)
                    result.Add(row with
                    {
                        AzureResourceId = res.ResourceId,
                        ResourceName = res.ResourceName,
                        ResourceType = res.ResourceType,
                    });
            }
            else
            {
                result.Add(row);
            }
        }
        return result;

        static string Key(string trackingId, string subscriptionId) =>
            $"{trackingId.Trim().ToLowerInvariant()}|{subscriptionId.Trim().ToLowerInvariant()}";
    }
}
