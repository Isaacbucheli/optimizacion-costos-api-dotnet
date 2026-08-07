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

/// <summary>
/// Una asignación RBAC efectiva de la última corrida finalizada de Revisión de accesos, ya
/// deduplicada (ver <see cref="Recolector.RbacRecolector"/> y
/// <see cref="OptimizacionCostos.Api.Features.Cdc.AccessReview.AccessReviewAssignments.Distinct"/>).
/// <see cref="RoleKey"/> es el GUID del rol sin el prefijo de suscripción que le agrega ARM
/// (mismo valor que agrupó la deduplicación); <see cref="Rol"/> es el nombre legible.
/// <see cref="SuscripcionesAlcanzadas"/> es el conjunto completo de suscripciones bajo las que ARM
/// reportó esta asignación: para <see cref="ScopeLevel"/> "root" o "management_group" son varias
/// (la asignación no "vive" en ninguna suscripción puntual), y <see cref="SubscriptionId"/> /
/// <see cref="SubscriptionName"/> quedan con el valor de la primera fila que ganó el dedup, que es
/// arbitrario para esos dos niveles: la calculadora decide cómo agrupar, usando el conjunto
/// completo en vez de esos dos campos sueltos.
/// <see cref="CuentaHabilitada"/> viaja como booleano, nunca como texto: un consumidor que
/// reconozca solo la palabra "enabled"/"habilitado" marcaría como deshabilitada toda cuenta cuyo
/// estado real es otro texto (el Excel del módulo escribe "Sí"/"No", por ejemplo).
/// <see cref="UltimoLoginTexto"/> es la fecha en ISO 8601 UTC tal cual, sin frase relativa ("hace
/// N días") ni clasificación de actividad: esa decisión depende de la fecha de corte del informe,
/// que solo conoce la calculadora.
/// </summary>
public sealed record RbacFila(
    string PrincipalObjectId,
    string? Nombre,
    string? Login,
    string PrincipalType,
    string Rol,
    string RoleKey,
    string Scope,
    string ScopeLevel,
    string? SubscriptionId,
    string? SubscriptionName,
    IReadOnlyList<string> SuscripcionesAlcanzadas,
    bool? CuentaHabilitada,
    string? UltimoLoginTexto,
    string? ViaGrupoId);
