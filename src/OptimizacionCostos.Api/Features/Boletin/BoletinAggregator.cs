namespace OptimizacionCostos.Api.Features.Boletin;

/// <summary>Fila leída de dbo.boletin_retirement (solo status='vigente').</summary>
public sealed record StoredRetirement(
    string FingerprintHex, string Source, string AnnouncementKey, string SubscriptionId,
    string? AzureResourceId, string ResourceName, string ResourceType, string RetiringFeature,
    DateOnly? RetirementDate, string Title, string? Summary, string? RecommendedAction, string? LearnMoreUrl);

/// <summary>Arma la vista del boletín: agrupa por anuncio y calcula KPIs al día de hoy. Puro.</summary>
public static class BoletinAggregator
{
    /// <summary>Excluye avisos de suscripciones que dejaron de estar administradas: siguen en BD
    /// (histórico) pero no deben aparecer en la vista ni inflar KPIs (impactadas > administradas).</summary>
    internal static IReadOnlyList<StoredRetirement> FilterToManaged(
        IReadOnlyList<StoredRetirement> rows, IEnumerable<string> managedSubscriptionIds)
    {
        var set = new HashSet<string>(managedSubscriptionIds, StringComparer.OrdinalIgnoreCase);
        return rows.Where(r => set.Contains(r.SubscriptionId)).ToList();
    }

    public static IReadOnlyDictionary<string, object?> BuildView(
        IReadOnlyList<StoredRetirement> rows, int subscriptionsTotal, DateOnly today)
    {
        var groups = rows
            .GroupBy(r => (r.Source, r.AnnouncementKey))
            .Select(g =>
            {
                var first = g.First();
                var resources = g.Where(r => r.AzureResourceId is not null).ToList();
                return new Dictionary<string, object?>
                {
                    ["source"] = first.Source,
                    ["announcement_key"] = first.AnnouncementKey,
                    ["title"] = first.Title,
                    ["retiring_feature"] = first.RetiringFeature,
                    ["retirement_date"] = first.RetirementDate?.ToString("yyyy-MM-dd"),
                    ["urgency"] = BoletinUrgency.Classify(first.RetirementDate, today),
                    ["recommended_action"] = first.RecommendedAction,
                    ["learn_more_url"] = first.LearnMoreUrl,
                    ["summary"] = first.Summary,
                    ["resource_count"] = resources.Count,
                    ["subscription_ids"] = g.Select(r => r.SubscriptionId)
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                    ["resources"] = resources.Select(r => new Dictionary<string, object?>
                    {
                        ["fingerprint"] = r.FingerprintHex,
                        ["subscription_id"] = r.SubscriptionId,
                        ["resource_id"] = r.AzureResourceId,
                        ["resource_name"] = r.ResourceName,
                        ["resource_type"] = r.ResourceType,
                    }).ToList(),
                };
            })
            .OrderBy(g => g["retirement_date"] is null)
            .ThenBy(g => g["retirement_date"] as string, StringComparer.Ordinal)
            .ToList();

        var impactedSubs = rows.Select(r => r.SubscriptionId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        return new Dictionary<string, object?>
        {
            ["kpis"] = new Dictionary<string, object?>
            {
                ["announcements"] = groups.Count,
                ["due_soon"] = groups.Count(g => (string?)g["urgency"] == BoletinUrgency.Proximo),
                ["already_retired"] = groups.Count(g => (string?)g["urgency"] == BoletinUrgency.Retirado),
                ["resources"] = rows.Count(r => r.AzureResourceId is not null),
                ["subscriptions_impacted"] = impactedSubs,
                ["subscriptions_total"] = subscriptionsTotal,
            },
            ["groups"] = groups,
        };
    }
}
