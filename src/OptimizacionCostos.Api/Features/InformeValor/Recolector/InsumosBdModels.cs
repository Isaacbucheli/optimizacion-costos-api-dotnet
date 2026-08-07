namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>
/// Un hallazgo activo de Azure Advisor, a nivel recomendación × recurso (el mismo grano que el
/// export de la matriz WAF: una fila por cada recurso afectado por una recomendación).
/// <see cref="PillarNumber"/> y <see cref="ImpactNumber"/> son la fuente de verdad para pilar e
/// impacto: <c>advisor_category</c> guarda el valor crudo de Azure sin traducir ni espaciar
/// (por ejemplo "OperationalExcellence"), y el filtro de la capa de dibujo del informe no lo
/// reconoce. <see cref="Pilar"/> e <see cref="Impacto"/> ya vienen traducidos a las mismas
/// etiquetas que ve el consultor en la pantalla de la matriz.
/// <see cref="Recomendacion"/> es el título que se muestra (curado a español cuando aplica) y
/// <see cref="RecomendacionEn"/> el original en inglés de Azure, si se conoce (solo lo siembra el
/// sync; puede ser null para canónicas que nacieron de Excel/legacy).
/// <see cref="AhorroAnual"/> puede venir sin <see cref="MonedaAhorro"/> cuando Azure no publicó la
/// moneda en <c>additional_info</c>: el consumidor decide cómo mostrarlo.
/// </summary>
public sealed record AdvisorFila(
    int PillarNumber,
    string Pilar,
    int? ImpactNumber,
    string Impacto,
    string Recomendacion,
    string? RecomendacionEn,
    int CanonicalId,
    string? MatrixCode,
    string? Source,
    string? SubscriptionId,
    string SubscriptionName,
    string? ResourceName,
    string ResourceType,
    decimal? AhorroAnual,
    string? MonedaAhorro);
