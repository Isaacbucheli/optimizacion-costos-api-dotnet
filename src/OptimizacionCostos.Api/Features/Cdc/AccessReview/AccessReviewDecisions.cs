namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

public static class AccessDecisionValues
{
    public const string Mantener = "mantener";
    public const string Revocar = "revocar";
    public const string Justificado = "justificado";

    public static bool IsValid(string? value) =>
        value is Mantener or Revocar or Justificado;
}

/// <summary>Decisión persistida sobre un acceso (o sobre un hallazgo de umbral, con FindingKey).
/// `RunsSince` = corridas del cliente posteriores a la que se decidió; 0 = se decidió en la actual.</summary>
public sealed record AccessDecision(
    string AccessKey, string PrincipalObjectId, string RoleKey, string Scope,
    string? FindingKey, string Decision, string? Note,
    string? DecidedBy, DateTimeOffset DecidedAt, int? DecidedRunId, int RunsSince);

/// <summary>Lo que llega del front para un acceso. La clave la calcula el backend a partir del
/// principal, el rol y el scope: duplicar el hash en TypeScript sería lógica espejo, y si las dos
/// implementaciones se separaran las decisiones se perderían sin ningún error visible.</summary>
public sealed record AccessDecisionInput(
    string PrincipalObjectId, string RoleDefinitionId, string Scope, string Decision, string? Note);

/// <summary>Resumen de decisiones de una cuenta, para la columna de la tabla de Cuentas.</summary>
public sealed record AccessDecisionSummary(
    int Pendientes, int Mantener, int Revocar, int Justificado);
