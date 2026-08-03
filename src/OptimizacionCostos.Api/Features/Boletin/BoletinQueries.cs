namespace OptimizacionCostos.Api.Features.Boletin;

/// <summary>KQL de Azure Resource Graph del boletín. Fuentes: recomendaciones de Advisor de la
/// subcategoría "Service Upgrade and Retirement" (ver
/// learn.microsoft.com/azure/advisor/advisor-how-to-use-service-upgrade-retirement-recommendations),
/// eventos de retiro de Service Health, y los recursos concretos impactados por esos eventos
/// (tipo hermano "impactedresources"), todos consultados vía Azure Resource Graph.</summary>
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

    /// <summary>Recursos concretos impactados por un evento de Service Health (tipo hermano
    /// "impactedresources" de <see cref="ServiceHealthRetirements"/>). Cobertura PARCIAL a propósito:
    /// no todos los avisos de Service Health publican el detalle de recursos (depende de cuánto
    /// detalle haya cargado Microsoft para ese evento en particular) — cuando no hay fila acá, el
    /// aviso se sigue mostrando a nivel de suscripción como antes (ver BoletinParsers.ExpandHealthRows).
    /// El id de cada fila tiene la forma
    /// /subscriptions/{sub}/providers/Microsoft.ResourceHealth/events/{trackingId}/impactedResources/{n};
    /// el trackingId se extrae en C# (no acá) partiendo el id por "/events/", para poder testear la
    /// extracción sin depender de Azure. Los nombres exactos de las propiedades bajo `properties`
    /// (targetResourceId/targetResourceType/resourceName) NO están confirmados contra datos reales
    /// (documentación pública limitada sobre este tipo de Resource Graph) — se leen defensivamente
    /// con tostring() y el parser (BoletinParsers.FromHealthImpactedRow) tolera que falten o vengan
    /// vacías. Verificar contra un tenant real antes de confiar ciegamente en el detalle de recursos.
    /// </summary>
    public const string ServiceHealthImpactedResources = """
        servicehealthresources
        | where type =~ 'microsoft.resourcehealth/events/impactedresources'
        | project id, subscriptionId,
            targetResourceId = tostring(properties.targetResourceId),
            targetResourceType = tostring(properties.targetResourceType),
            resourceName = tostring(properties.resourceName)
        """;

    /// <summary>Runtimes de sitios Linux (App Service / Functions). LinuxFxVersion vive en
    /// siteProperties.properties; validado en vivo contra Resource Graph el 2026-08-01.</summary>
    public const string LinuxSiteRuntimes = """
        resources
        | where type =~ 'microsoft.web/sites'
        | mv-expand prop = properties.siteProperties.properties
        | where tostring(prop.name) == 'LinuxFxVersion' and tostring(prop.value) != ''
        | project subscriptionId,
            siteId = tolower(id),
            name,
            runtime = tostring(prop.value),
            siteKind = tostring(kind)
        """;

    /// <summary>Sitios Windows (el runtime NO está en ARG: se resuelve por ARM, ver SiteRuntimeArmClient).</summary>
    public const string WindowsSites = """
        resources
        | where type =~ 'microsoft.web/sites'
        | where kind !contains 'linux'
        | project subscriptionId,
            siteId = tolower(id),
            name,
            siteKind = tostring(kind)
        """;

    /// <summary>SO por VM. osName/osVersion vienen del instanceView que reporta el AGENTE de la VM
    /// (validado en vivo 2026-08-03: "Windows Server 2012 R2 Standard", "ubuntu 22.04"; funciona
    /// también en VMs migradas sin imageReference). VM sin agente/apagada larga → osName vacío
    /// y se ignora (no inventamos SO).</summary>
    public const string VmOsInventory = """
        resources
        | where type =~ 'microsoft.compute/virtualmachines'
        | project subscriptionId,
            vmId = tolower(id),
            name,
            osName = tostring(properties.extended.instanceView.osName),
            osVersion = tostring(properties.extended.instanceView.osVersion)
        """;

    /// <summary>Imagen SQL de los SQL VMs registrados (validado en vivo: "SQL2012-WS2012R2").
    /// Solo cubre SQL Server REGISTRADO como SQL VM; instalaciones manuales no registradas no
    /// aparecen (limitación honesta, documentada en la pestaña).</summary>
    public const string SqlVmImages = """
        resources
        | where type =~ 'microsoft.sqlvirtualmachine/sqlvirtualmachines'
        | project subscriptionId,
            sqlVmId = tolower(id),
            name,
            sqlImageOffer = tostring(properties.sqlImageOffer)
        """;
}
