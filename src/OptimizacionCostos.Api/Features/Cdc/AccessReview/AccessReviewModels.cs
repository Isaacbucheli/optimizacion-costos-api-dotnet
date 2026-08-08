namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

/// <summary>Corrida de revisión de accesos. status: queued|running|ok|partial|error.</summary>
public sealed record AccessRunRef(
    int RunId, int ClientId, string Status,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error, string? RequestedBy);

/// <summary>Estado por credencial dentro de una corrida.
/// arm_status: ok|error. graph_status: ok|sin_consent|sin_licencia_p1|error|no_aplica.</summary>
public sealed record AccessCredStatus(
    int CredentialId, string? CredentialName, string ArmStatus, string GraphStatus, string? Detail);

/// <summary>Asignación RBAC efectiva. ViaGroup* != null cuando la fila viene de expandir un grupo.
/// RoleClass: owner|otorga_accesos|escritura_total|escritura_servicio|lectura, o null si la
/// definición de rol no era resoluble en la suscripción (corridas viejas también vienen en null).</summary>
public sealed record AccessAssignmentRow(
    string SubscriptionId, string? SubscriptionName, string? SubscriptionState,
    string Scope, string ScopeLevel, string RoleName, string RoleDefinitionId,
    string PrincipalObjectId, string PrincipalType, string? DisplayName, string? Login,
    string? UserType, string? ViaGroupId, string? ViaGroupName,
    bool? AccountEnabled, DateTimeOffset? LastSignIn, string? MfaStatus,
    string? RoleClass = null, bool IsCustomRole = false,
    /// <summary>Suscripciones bajo las que ARM reportó esta asignación. Lo llena
    /// <see cref="AccessReviewAssignments.Distinct"/> al colapsar las repeticiones de una asignación
    /// heredada: más de una significa que el acceso alcanza varias suscripciones, no que existan
    /// varias asignaciones. Vacío en las filas crudas recién leídas de ARM.</summary>
    IReadOnlyList<string>? SeenInSubscriptions = null,
    /// <summary>Ambiente ya resuelto para esta fila. Lo llena
    /// <see cref="AccessReviewAssignments.Distinct"/>, que es el único lugar que conoce TODAS las
    /// suscripciones alcanzadas por un acceso heredado. Null en filas crudas: ahí se cae al nombre de
    /// la suscripción, que para un scope de suscripción o menor es el dato correcto.</summary>
    string? Environment = null,
    /// <summary>Nombre de cada suscripción de <see cref="SeenInSubscriptions"/>, en la misma
    /// posición (índice a índice). Lo llena <see cref="AccessReviewAssignments.Distinct"/> con el
    /// mismo dato que ya usaba, sin exponer, para resolver <see cref="Environment"/>: cada
    /// repetición cruda de una asignación heredada trae el nombre de la suscripción bajo la que
    /// ARM la reportó, así que el nombre de una suscripción alcanzada solo por herencia (nunca vista
    /// como scope directo de ninguna fila) ya está disponible acá, no hace falta reconstruirlo
    /// buscando otra fila cuyo <see cref="SubscriptionId"/> coincida. Puede traer <c>null</c> en una
    /// posición puntual si esa repetición en particular no vino con nombre; <c>null</c> en la lista
    /// completa (no solo una posición) en filas crudas, igual que <see cref="SeenInSubscriptions"/>.
    /// </summary>
    IReadOnlyList<string?>? SeenInSubscriptionNames = null);

public sealed record AccessGuestRow(
    string ObjectId, string? DisplayName, string? Email, string? ExternalDomain,
    bool AccountEnabled, string? ExternalState, DateTimeOffset? CreatedAtAzure,
    DateTimeOffset? LastSignIn, string? RolesInSubs, string? MfaStatus);

public sealed record AccessGlobalAdminRow(
    string ObjectId, string? DisplayName, string? Upn, string? UserType,
    bool? AccountEnabled, DateTimeOffset? LastSignIn, string? MfaStatus);

/// <summary>Snapshot completo de una corrida (lo que consume la UI y el Excel).</summary>
public sealed record AccessReviewSnapshot(
    AccessRunRef Run,
    IReadOnlyList<AccessCredStatus> Credentials,
    IReadOnlyList<AccessAssignmentRow> Assignments,
    IReadOnlyList<AccessGuestRow> Guests,
    IReadOnlyList<AccessGlobalAdminRow> GlobalAdmins);

/// <summary>Una cuenta (principal) con sus asignaciones efectivas agregadas: la unidad de lectura
/// primaria del módulo. IsExternal es null cuando no se pudo medir (sin Graph) o cuando el eje no
/// aplica al tipo. Via: directo | grupo | ambos.</summary>
public sealed record AccessAccountRow(
    string PrincipalObjectId, string PrincipalType, string? DisplayName, string? Login, string? UserType,
    bool? IsExternal, int TotalAssignments,
    int Owner, int OtorgaAccesos, int EscrituraTotal, int EscrituraServicio, int Lectura, int SinClasificar,
    int Subscriptions, string BroadestScopeLevel, string Via,
    bool? AccountEnabled, DateTimeOffset? LastSignIn, string? MfaStatus, bool Orphan,
    AccessDecisionSummary? Decisions = null);

/// <summary>Los contadores de privilegio (elevadas, owners, roles personalizados) solo dependen de
/// ARM: siguen midiéndose aunque la fase Graph haya fallado. Los de externos dependen de Graph.</summary>
public sealed record AccessReviewKpis(
    int GlobalAdmins, int GlobalAdminsSinMfa, int InternosSinMfaConRbac,
    int CuentasDeshabilitadasConRbac, int CuentasInactivasConRbac,
    int GuestsTotal, int GuestsInactivos, int GuestsInactivosConPermisos,
    int ServicePrincipalsUnicos, int TotalAsignaciones,
    int CuentasUnicas, int AsignacionesElevadas, decimal PctElevadas, int Owners,
    int CuentasExternasConRbac, int OwnersExternos, int RolesPersonalizados,
    // Accesos con privilegio elevado que todavia nadie decidio: la cola de trabajo real.
    int PendientesDeRevisar);
