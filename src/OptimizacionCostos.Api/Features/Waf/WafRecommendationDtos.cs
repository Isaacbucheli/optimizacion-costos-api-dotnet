namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// DTOs de respuesta agregada del módulo WAF que producen los stores de recomendaciones
/// (summary/sections). Forma EXACTA portada de waf_client_summary / waf_client_sections del
/// FastAPI (app/routes/waf.py). El serializador global ya aplica snake_case.
/// </summary>

/// <summary>Última ingestión del cliente (sub-objeto de WafClientSummary). Null si no hubo.</summary>
public sealed record WafLatestIngestion(
    int RunId,
    string SourceFileName,
    string Status,
    int? RowsTotal,
    int? RowsProcessed,
    string StartedAt,
    string? CompletedAt);

/// <summary>
/// Resumen de la matriz del cliente: totales de recomendaciones/findings + última ingestión.
/// Port de waf_client_summary.
/// </summary>
public sealed record WafClientSummary(
    int ClientId,
    int Recommendations,
    int ActiveRecommendations,
    int CostRecommendations,
    int ActiveFindings,
    WafLatestIngestion? LatestIngestion);

/// <summary>
/// Tarjeta por pilar (sección) para la matriz. Siempre se devuelven las 5 (pilares sin datos
/// quedan en cero). Port de waf_client_sections.
/// </summary>
public sealed record WafSection(
    int SectionNum,
    string SectionName,
    int TotalRecs,
    int TotalResources,
    double AvgProgress,
    int HighRecs,
    int MediumRecs);

/// <summary>
/// Opción del filtro por suscripción. Sale de los hallazgos activos, no de
/// client_azure_subscriptions: incluye los placeholders de los clientes migrados del CDC.
/// </summary>
public sealed record WafSubscriptionOption(
    string SubscriptionId,
    string SubscriptionName,
    int Recommendations,
    int Resources);
