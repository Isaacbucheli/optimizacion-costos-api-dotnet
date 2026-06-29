using Azure.Core;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Alertas disparadas en el período (Azure Monitor Alerts Management API).
/// Port 1:1 de app/reports/alerts.py.
///
/// Consume GET /subscriptions/{sub}/providers/Microsoft.AlertsManagement/alerts con timeRange=30d
/// (valor enumerado que la API siempre acepta) y filtra los disparos por fecha
/// (essentials.startDateTime) en el cliente. La API retiene solo alertas de los últimos 30 días.
/// Solo se incluyen alertas cuya regla comienza por 'BIT' (las que BIT administra y notifica).
/// </summary>
public interface IReportAlerts
{
    /// <summary>
    /// Alertas disparadas en el período, agregadas por tipo y suscripción. Port de fetch_fired_alerts.
    ///
    /// Devuelve un dict con la forma del Python:
    /// - {error: mensaje} si la consulta falló en todas las suscripciones o la ventana quedó fuera
    ///   de la retención de 30 días;
    /// - de lo contrario {resumen, por_tipo, por_suscripcion} (resumen en cero si no hubo disparos),
    ///   más una clave 'nota' cuando la ventana es parcial.
    /// </summary>
    Task<IReadOnlyDictionary<string, object?>> FetchFiredAlertsAsync(
        TokenCredential credential,
        IReadOnlyList<string> subscriptions,
        IReadOnlyDictionary<string, string> subNameMap,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default);
}
