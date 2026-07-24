namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

/// <summary>Corrida de revisión de accesos. status: queued|running|ok|partial|error.</summary>
public sealed record AccessRunRef(
    int RunId, int ClientId, string Status,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error, string? RequestedBy);

/// <summary>Estado por credencial dentro de una corrida.
/// arm_status: ok|error. graph_status: ok|sin_consent|sin_licencia_p1|error|no_aplica.</summary>
public sealed record AccessCredStatus(
    int CredentialId, string? CredentialName, string ArmStatus, string GraphStatus, string? Detail);

/// <summary>Asignación RBAC efectiva. ViaGroup* != null cuando la fila viene de expandir un grupo.</summary>
public sealed record AccessAssignmentRow(
    string SubscriptionId, string? SubscriptionName, string? SubscriptionState,
    string Scope, string ScopeLevel, string RoleName, string RoleDefinitionId,
    string PrincipalObjectId, string PrincipalType, string? DisplayName, string? Login,
    string? UserType, string? ViaGroupId, string? ViaGroupName,
    bool? AccountEnabled, DateTimeOffset? LastSignIn, string? MfaStatus);

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

public sealed record AccessReviewKpis(
    int GlobalAdmins, int GlobalAdminsSinMfa, int InternosSinMfaConRbac,
    int CuentasDeshabilitadasConRbac, int CuentasInactivasConRbac,
    int GuestsTotal, int GuestsInactivos, int GuestsInactivosConPermisos,
    int ServicePrincipalsUnicos, int TotalAsignaciones);
