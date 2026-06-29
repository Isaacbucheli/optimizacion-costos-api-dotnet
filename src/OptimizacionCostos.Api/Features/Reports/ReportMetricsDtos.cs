namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Recurso objetivo para la extracción de métricas. Port de los dicts que el builder arma en
/// <c>targets</c> (app/reports/builder.py): azure_resource_id, resource_name, resource_type,
/// subscription_name, resource_group. subscription_name/resource_group pueden venir null
/// (SQL MI / App Service plans no setean resource_group; igual que el .get() del Python).
/// </summary>
public sealed record MetricTarget(
    string AzureResourceId,
    string? ResourceName,
    string ResourceType,
    string? SubscriptionName,
    string? ResourceGroup = null);

/// <summary>
/// Ventana del mes calendario. Port de month_timespan: [start, end) en UTC; si el mes está en
/// curso, end = ahora y partial = true.
/// </summary>
public sealed record MonthWindow(DateTimeOffset Start, DateTimeOffset End, bool Partial);
