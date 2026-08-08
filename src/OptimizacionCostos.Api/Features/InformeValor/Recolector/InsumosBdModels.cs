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
/// <see cref="ResourceGroup"/> (entrega 2b, D11) más <see cref="SubscriptionName"/> y
/// <see cref="ResourceName"/> forman la identidad completa de un recurso: dos recursos con el
/// mismo nombre en grupos o suscripciones distintos no son el mismo recurso, y los nombres
/// genéricos de Azure colisionan seguido. <c>NOT NULL</c> en <c>waf_resource_finding</c>, a
/// diferencia de <see cref="ResourceName"/>, que sí puede venir null.
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
    string ResourceGroup,
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
/// <see cref="RoleClass"/> e <see cref="IsCustomRole"/> son la clasificación que Revisión de
/// accesos ya calcula por los permisos reales del rol (<c>AccessReviewRoleClassifier</c>: owner,
/// otorga_accesos, escritura_total, escritura_servicio, lectura, o null si el rol no era
/// resoluble) y persiste en <c>cdc_access_assignment</c>. Sin estos dos campos, la calculadora del
/// informe tendría que portar el regex de la plantilla sobre el nombre del rol en inglés, y
/// contradeciría a Revisión de accesos justo en los roles personalizados: <c>IsCustomRole</c> es
/// exactamente la señal de que ese regex no los puede reconocer.
///
/// <para><see cref="SuscripcionesAlcanzadasNombres"/> (Tarea 8 del informe de valor) es el nombre
/// de cada id de <see cref="SuscripcionesAlcanzadas"/>, posición a posición: viene de
/// <see cref="OptimizacionCostos.Api.Features.Cdc.AccessReview.AccessAssignmentRow.SeenInSubscriptionNames"/>,
/// que ya calculaba <c>AccessReviewAssignments.Distinct</c> para resolver el ambiente y hasta esta
/// tarea no exponía. Sin este campo, el bloque de seguridad solo puede nombrar una suscripción
/// alcanzada si ALGUNA fila la trae como su propia <see cref="SubscriptionId"/>/<see cref="SubscriptionName"/>
/// primaria — falso para una suscripción alcanzada solo por herencia de <c>root</c>/
/// <c>management_group</c> que nunca aparece como scope directo de ninguna asignación.</para>
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
    IReadOnlyList<string?> SuscripcionesAlcanzadasNombres,
    bool? CuentaHabilitada,
    string? UltimoLoginTexto,
    string? ViaGrupoId,
    string? RoleClass,
    bool IsCustomRole);

/// <summary>
/// Un retiro de Azure vigente para un cliente, agrupado por anuncio (<see cref="AnnouncementKey"/>):
/// <see cref="RecursosAfectados"/> cuenta cuántos recursos concretos toca ese anuncio, no cuántas
/// veces aparece. La fuente es <c>boletin_retirement</c> del módulo Boletín (no el cruce de
/// Advisor de la matriz, que no trae fecha de retiro ni acción recomendada por recurso).
/// <see cref="Titulo"/> y <see cref="AccionRecomendada"/> prefieren la traducción al español
/// cuando ya existe (<c>title_es</c>/<c>recommended_action_es</c>) y caen al original en inglés si
/// todavía no se tradujo, para no publicar un campo vacío mientras la traducción está pendiente.
/// <see cref="Caracteristica"/> es <c>retiring_feature</c> ("Retiring Feature" del CSV de Advisor).
/// <b>Este recolector no clasifica el plazo</b> (vencido, menos de tres meses, menos de un año):
/// esa regla depende de la fecha de corte del informe, que solo conoce la calculadora. Por eso el
/// record no tiene ni "Situación" ni "Vencido", solo <see cref="FechaRetiro"/> cruda.
/// </summary>
public sealed record RetiroFila(
    string AnnouncementKey,
    string? Caracteristica,
    DateOnly? FechaRetiro,
    string? Titulo,
    string? AccionRecomendada,
    int RecursosAfectados);
