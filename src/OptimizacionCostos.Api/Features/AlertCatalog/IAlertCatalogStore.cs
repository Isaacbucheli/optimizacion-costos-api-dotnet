namespace OptimizacionCostos.Api.Features.AlertCatalog;

/// <summary>
/// Repositorio del catalogo de alertas. Equivale a app/alerts/store.py.
/// El update recibe solo las columnas presentes (semantica exclude_unset).
/// </summary>
public interface IAlertCatalogStore
{
    Task<IReadOnlyList<AlertItem>> ListAlertsAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<AlertItem?> GetAlertAsync(int alertId, CancellationToken ct = default);
    Task<int> CreateAlertAsync(AlertCreate data, CancellationToken ct = default);
    Task<bool> UpdateAlertAsync(int alertId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task<bool> SoftDeleteAlertAsync(int alertId, CancellationToken ct = default);

    Task<IReadOnlyList<KqlItem>> ListKqlAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<KqlItem?> GetKqlAsync(int kqlId, CancellationToken ct = default);
    Task<int> CreateKqlAsync(KqlCreate data, CancellationToken ct = default);
    Task<bool> UpdateKqlAsync(int kqlId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task<bool> SoftDeleteKqlAsync(int kqlId, CancellationToken ct = default);
}

/// <summary>Columnas mutables permitidas (whitelist anti-inyeccion al construir SET).</summary>
public static class AlertColumns
{
    public static readonly string[] Alert =
    [
        "alert_number", "name", "resource", "alert_type", "description",
        "severity", "origin", "detail", "action_group", "kql_code",
        "technical_requirement",
    ];

    public static readonly string[] Kql = ["name", "description", "kql_query"];

    /// <summary>
    /// Ancho de las columnas acotadas de dbo.alert_catalog (ver AlertCatalogSchema). Solo las
    /// NVARCHAR(n): las NVARCHAR(MAX) se omiten a propósito y alert_number es INT. Si cambia el
    /// esquema hay que mover este mapa con él — es lo que evita el 8152 en el UPDATE.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> AlertMaxLengths = new Dictionary<string, int>
    {
        ["name"] = 300,
        ["resource"] = 120,
        ["alert_type"] = 80,
        ["severity"] = 40,
        ["origin"] = 120,
        ["action_group"] = 300,
    };

    /// <summary>Ancho de las columnas acotadas de dbo.alert_kql_library (description y kql_query son MAX).</summary>
    public static readonly IReadOnlyDictionary<string, int> KqlMaxLengths = new Dictionary<string, int>
    {
        ["name"] = 200,
    };
}
