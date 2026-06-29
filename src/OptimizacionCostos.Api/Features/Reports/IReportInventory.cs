using Azure.Core;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Inventario vivo para el informe mensual (Resource Graph + ARM). Port 1:1 de
/// app/reports/azure_inventory.py. Un método por cada función del Python; mismas KQL, api-versions,
/// normalizaciones y nombres de campos (los consume el builder y el front).
///
/// Los fetchers reciben la <see cref="TokenCredential"/> ya construida (igual que en Python), las
/// suscripciones y el mapa subId→nombre. El orquestador (builder) arma la credencial y se la pasa.
/// Las KQL se ejecutan vía <see cref="AzureIntegration.IResourceGraphRunner"/>; el ARM REST directo
/// (vmSizes) usa IHttpClientFactory + el token Bearer.
/// </summary>
public interface IReportInventory
{
    Task<IReadOnlyList<VmInventoryItem>> FetchVmInventoryAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions,
        IReadOnlyDictionary<string, string> subNameMap, CancellationToken ct = default);

    /// <summary>Mapa size_name → {vcpu, ram_gb} consultando ARM vmSizes por región. Usa el token Bearer.</summary>
    Task<IReadOnlyDictionary<string, VmSizeInfo>> FetchVmSizesAsync(
        string token, IReadOnlyList<string> subscriptions, IReadOnlyList<string> locations,
        CancellationToken ct = default);

    /// <summary>Mapa targetResourceId(lower) → availabilityState. Degrada a vacío si falla.</summary>
    Task<IReadOnlyDictionary<string, string>> FetchResourceHealthAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, CancellationToken ct = default);

    Task<IReadOnlyList<ServiceStatusItem>> FetchServiceStatusAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, CancellationToken ct = default);

    Task<BackupResult> FetchBackupsAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, DateTime start, DateTime end,
        IReadOnlyDictionary<string, string>? subNameMap = null, CancellationToken ct = default);

    Task<IReadOnlyList<AppServicePlanItem>> FetchAppServicePlansAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, CancellationToken ct = default);

    Task<IReadOnlyList<ConnectivityItem>> FetchConnectivityAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, CancellationToken ct = default);

    Task<IReadOnlyList<SqlMiItem>> FetchSqlMiInventoryAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, CancellationToken ct = default);

    Task<IReadOnlyList<ServiceHealthEvent>> FetchServiceHealthEventsAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions, IReadOnlyList<string> regions,
        DateTime start, DateTime end, CancellationToken ct = default);

    /// <summary>
    /// Resumen de asignaciones RBAC a nivel de suscripción (sin detalle de RG/recurso). Devuelve
    /// { resumen, top_roles, por_suscripcion } como en Python; vacío si no hay permiso/filas.
    /// </summary>
    Task<IReadOnlyDictionary<string, object?>> FetchRoleAssignmentsAsync(
        TokenCredential credential, IReadOnlyList<string> subscriptions,
        IReadOnlyDictionary<string, string> subNameMap, CancellationToken ct = default);
}
