using Azure.Core;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Extractor de métricas de Azure Monitor para el informe mensual.
/// Port 1:1 de app/reports/metrics_extractor.py.
///
/// Consume la API REST de métricas (microsoft.insights/metrics) con el token ARM de la credencial
/// del cliente. Una llamada por recurso: la API permite pedir varias métricas y agregaciones del
/// mismo namespace en un solo request con grano diario (interval=P1D).
///
/// El Service Principal del cliente necesita el rol Monitoring Reader (además de Reader) sobre las
/// suscripciones; si una métrica no está disponible se omite sin abortar la extracción completa.
/// </summary>
public interface IReportMetrics
{
    /// <summary>
    /// Período del 1 al último día del mes (UTC). Si el mes está en curso, el fin es 'ahora' y se
    /// marca el informe como parcial. Port de month_timespan(year, month).
    /// </summary>
    MonthWindow MonthTimespan(int year, int month);

    /// <summary>
    /// Extrae métricas para los recursos soportados. Port de extract_metrics(credential, resources,
    /// start, end). Devuelve un dict con claves virtual_machines / sql_managed_instances /
    /// app_service_plans (listas de dicts por recurso con cpu_avg/cpu_max/ram/... + serie daily) y
    /// errors (lista de strings). Errores por recurso se acumulan en 'errors' sin abortar.
    /// </summary>
    Task<IReadOnlyDictionary<string, object?>> ExtractMetricsAsync(
        TokenCredential credential,
        IReadOnlyList<MetricTarget> targets,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default);
}
