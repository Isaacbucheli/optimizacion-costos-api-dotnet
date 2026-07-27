namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

/// <summary>
/// Hallazgo de una corrida. `Evaluable = false` cuando la regla depende de datos que la corrida no
/// midió (Graph incompleto, sin licencia P1): en ese caso los conteos van en 0 y la UI lo muestra
/// como "no evaluable", nunca como un cero limpio.
/// `AffectedPrincipals` va vacío en las reglas de práctica (porcentajes): un umbral no tiene culpables.
/// </summary>
public sealed record AccessFinding(
    string Key, string Severity, string Title, string Detail, string Recommendation,
    bool Evaluable, string? NotEvaluableReason,
    int AffectedAccounts, int AffectedAssignments,
    IReadOnlyList<string> AffectedPrincipals);

public static class AccessFindingSeverity
{
    public const string Critica = "critica";
    public const string Alta = "alta";
    public const string Media = "media";
    public const string Informativa = "informativa";

    /// <summary>Orden de presentación: primero lo que hay que mirar hoy.</summary>
    public static int Rank(string severity) => severity switch
    {
        Critica => 0, Alta => 1, Media => 2, _ => 3,
    };
}

public static class AccessFindingThresholds
{
    /// <summary>Global Admins permanentes recomendados por Microsoft.</summary>
    public const int MaxGlobalAdmins = 5;

    /// <summary>Criterio propio, no recomendación de Microsoft: por encima de esto la administración
    /// de accesos es por persona y no por grupo, y no escala.</summary>
    public const double DirectAssignmentShare = 0.70;

    /// <summary>Criterio propio: tanta asignación a nivel de recurso vuelve el acceso ingobernable.</summary>
    public const double ResourceScopeShare = 0.30;
}
