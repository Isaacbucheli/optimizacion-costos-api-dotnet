namespace OptimizacionCostos.Api.Features.Boletin;

/// <summary>Fila leída de dbo.boletin_retirement (solo status='vigente'). Los campos *_es son la
/// traducción fiel del original en inglés (NULL si aún no se tradujo o la IA no está configurada;
/// el front cae al texto EN en ese caso).</summary>
public sealed record StoredRetirement(
    string FingerprintHex, string Source, string AnnouncementKey, string SubscriptionId,
    string? AzureResourceId, string ResourceName, string ResourceType, string RetiringFeature,
    DateOnly? RetirementDate, string Title, string? Summary, string? RecommendedAction, string? LearnMoreUrl,
    string? TitleEs = null, string? SummaryEs = null, string? RecommendedActionEs = null,
    bool Derived = false);

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
        // Task 5: separar filas de retiros (advisor/service_health) de fin de soporte (eol)
        var retiroRows = rows.Where(r => r.Source != "eol").ToList();
        var eolRows = rows.Where(r => r.Source == "eol").ToList();

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
                    ["title_es"] = g.Select(r => r.TitleEs).FirstOrDefault(t => !string.IsNullOrEmpty(t)),
                    ["summary_es"] = g.Select(r => r.SummaryEs).FirstOrDefault(t => !string.IsNullOrEmpty(t)),
                    ["recommended_action_es"] = g.Select(r => r.RecommendedActionEs).FirstOrDefault(t => !string.IsNullOrEmpty(t)),
                    ["resource_count"] = resources.Count,
                    // Derived (Task 5): recursos que NO vinieron de Microsoft (enriquecimiento de
                    // Service Health) sino inferidos por los detectores de inventario del BIT
                    // (runtime matching). Se cuentan aparte para que la UI pueda distinguir
                    // "confirmado por Azure" de "inferido" sin tener que iterar resources[].
                    ["derived_resource_count"] = resources.Count(r => r.Derived),
                    ["subscription_ids"] = g.Select(r => r.SubscriptionId)
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                    ["resources"] = resources.Select(r => new Dictionary<string, object?>
                    {
                        ["fingerprint"] = r.FingerprintHex,
                        ["subscription_id"] = r.SubscriptionId,
                        ["resource_id"] = r.AzureResourceId,
                        ["resource_name"] = r.ResourceName,
                        ["resource_type"] = r.ResourceType,
                        ["derived"] = r.Derived,
                    }).ToList(),
                };
            })
            .OrderBy(g => g["retirement_date"] is null)
            .ThenBy(g => g["retirement_date"] as string, StringComparer.Ordinal)
            .ToList();

        // KPIs sobre retiros (sin contar eol)
        var retiroGroups = groups.Where(g => (string?)g["source"] != "eol").ToList();
        var impactedSubs = retiroRows.Select(r => r.SubscriptionId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        return new Dictionary<string, object?>
        {
            ["kpis"] = new Dictionary<string, object?>
            {
                ["announcements"] = retiroGroups.Count,
                ["due_soon"] = retiroGroups.Count(g => (string?)g["urgency"] == BoletinUrgency.Proximo),
                ["already_retired"] = retiroGroups.Count(g => (string?)g["urgency"] == BoletinUrgency.Retirado),
                ["resources"] = retiroRows.Count(r => r.AzureResourceId is not null),
                ["subscriptions_impacted"] = impactedSubs,
                ["subscriptions_total"] = subscriptionsTotal,
                // Task 5: KPIs para fin de soporte
                ["eol_products"] = groups.Count(g => (string?)g["source"] == "eol"),
                ["eol_resources"] = eolRows.Count(r => r.AzureResourceId is not null),
            },
            ["groups"] = groups,
        };
    }

    /// <summary>Arma el <c>subscriptions</c> top-level de la vista (A2): id + nombre visible de cada
    /// suscripción administrada, para que el front muestre el nombre en vez del GUID. Si no hay
    /// nombre sembrado (sync ARM todavía no corrió, o vino vacío), cae al propio subscription_id
    /// para no dejar la columna en blanco.</summary>
    public static IReadOnlyList<Dictionary<string, object?>> BuildSubscriptionsView(
        IReadOnlyList<(string SubscriptionId, string? Name)> rows) =>
        rows.Select(r => new Dictionary<string, object?>
        {
            ["subscription_id"] = r.SubscriptionId,
            ["name"] = string.IsNullOrWhiteSpace(r.Name) ? r.SubscriptionId : r.Name,
        }).ToList();
}
