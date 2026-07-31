namespace OptimizacionCostos.Api.Features.Boletin;

/// <summary>KQL de Azure Resource Graph del boletín. Fuentes: recomendaciones de Advisor de la
/// subcategoría "Service Upgrade and Retirement" (ver
/// learn.microsoft.com/azure/advisor/advisor-how-to-use-service-upgrade-retirement-recommendations)
/// y eventos de retiro de Service Health, ambos consultados vía Azure Resource Graph.</summary>
public static class BoletinQueries
{
    /// <summary>Recomendaciones Advisor "Service Upgrade and Retirement" ligadas a un retiro real
    /// (retirementFeatureName vacío = upgrade sin retiro, se excluye como indica la doc oficial).</summary>
    public const string AdvisorRetirements = """
        advisorresources
        | where type =~ 'microsoft.advisor/recommendations'
        | where properties.category == 'HighAvailability'
        | where tostring(properties.extendedProperties.recommendationSubCategory) == 'ServiceUpgradeAndRetirement'
        | where tostring(properties.extendedProperties.retirementFeatureName) != ''
        | project subscriptionId,
            resourceId = tostring(properties.resourceMetadata.resourceId),
            impactedField = tostring(properties.impactedField),
            impactedValue = tostring(properties.impactedValue),
            problem = tostring(properties.shortDescription.problem),
            solution = tostring(properties.shortDescription.solution),
            retirementDate = tostring(properties.extendedProperties.retirementDate),
            retiringFeature = tostring(properties.extendedProperties.retirementFeatureName),
            learnMore = tostring(properties.learnMoreLink)
        """;

    /// <summary>Eventos de retiro de Service Health. Deliberadamente NO filtra
    /// <c>impactMitigationTime > now()</c>: también se persisten los eventos ya vencidos, porque la
    /// retención de Service Health es de ~60 días (Azure los retira del feed) y el boletín necesita
    /// conservar el histórico y clasificarlos como "retirado" en vez de perderlos silenciosamente.</summary>
    public const string ServiceHealthRetirements = """
        servicehealthresources
        | where type =~ 'microsoft.resourcehealth/events'
        | where tostring(properties.EventType) == 'HealthAdvisory' and tostring(properties.EventSubType) == 'Retirement'
        | project subscriptionId,
            trackingId = tostring(properties.TrackingId),
            title = tostring(properties.Title),
            summary = tostring(properties.Summary),
            impactMitigationTime = tostring(todatetime(tolong(properties.ImpactMitigationTime)))
        """;
}
