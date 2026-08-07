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

/// <summary>
/// Una recomendación de la matriz WAF de un cliente, al grano recomendación × canónica × tracking
/// (una fila por recomendación, no por recurso: <see cref="ResourceCount"/> ya viene contado).
/// <see cref="Hallazgo"/> es <c>review_scope_es</c> ("título/ámbito" curado que ve el consultor en
/// la pantalla de la matriz), y <see cref="Ambito"/> es la etiqueta de pilar (misma tabla que usa
/// <see cref="AdvisorRecolector.EtiquetaPilar"/>): dos conceptos distintos con nombres que se
/// prestan a confundirse.
/// <see cref="EsfuerzoTexto"/> es <c>projected_bit_effort</c> tal cual lo escribió el consultor
/// ("2-3 días", "medio día"): no se parsea a número acá. <see cref="AvancePct"/> es
/// <c>completion_pct</c> de <c>waf_recommendation_tracking</c>, 0 si el cliente todavía no tiene
/// fila de tracking. <see cref="Prioridad"/> es <c>priority_override</c> tal cual (el número que
/// el consultor eligió, sin la etiqueta "1 - ALTA" que arma el exportador de Excel): esa traducción
/// es una decisión de presentación de la calculadora, no de este recolector.
/// <see cref="Excluida"/> es <c>is_excluded</c> de la canónica: se cuenta para la trazabilidad, no
/// filtra ninguna fila (la pantalla de la matriz tampoco lo hace).
/// </summary>
public sealed record MatrizFila(
    int CanonicalId,
    string? MatrixCode,
    int PillarNumber,
    string Ambito,
    string Hallazgo,
    DateOnly? Fecha,
    int? ImpactNumber,
    string? Prioridad,
    string? EsfuerzoTexto,
    int AvancePct,
    string? Registro,
    int ResourceCount,
    bool Excluida);
