using System.Globalization;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Features.Boletin;

/// <summary>Convierte filas de las KQL del boletín a RetirementRow. Puro y testeable sin Azure.</summary>
public static class BoletinParsers
{
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
        return new RetirementRow(
            Source: RetirementRow.SourceServiceHealth,
            AnnouncementKey: tracking,
            SubscriptionId: subId,
            AzureResourceId: null,
            ResourceName: "",
            ResourceType: "",
            RetiringFeature: "",
            RetirementDate: ParseDate(row.Str("impactMitigationTime")),
            Title: (row.Str("title") is { Length: > 0 } t ? t : tracking).Trim(),
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
}
