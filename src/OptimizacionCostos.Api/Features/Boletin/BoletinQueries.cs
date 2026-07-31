namespace OptimizacionCostos.Api.Features.Boletin;

/// <summary>KQL de Azure Resource Graph del boletín (fuentes validadas en
/// docs/2026-07-31-analisis-boletin-recomendaciones-azure.md del repo de trabajo).</summary>
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

    /// <summary>Eventos de retiro de Service Health. Retención ~60 días en Azure: por eso se persisten.</summary>
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
