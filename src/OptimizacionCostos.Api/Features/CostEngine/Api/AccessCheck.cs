namespace OptimizacionCostos.Api.Features.CostEngine.Api;

/// <summary>
/// Resultado de una verificacion de acceso por cliente. En vez de lanzar HTTPException
/// como el FastAPI (access_control.py), se devuelve un resultado que el controller
/// traduce a Forbid()/NotFound()/Ok.
/// </summary>
public enum AccessResult
{
    /// <summary>El usuario puede acceder al recurso.</summary>
    Ok,

    /// <summary>El recurso no existe (analysis / cost_result) -> 404.</summary>
    NotFound,

    /// <summary>El recurso existe pero el cliente no es accesible -> 403.</summary>
    Forbidden,
}

/// <summary>Resultado tipado de una asercion de acceso (paridad con assert_* del Python).</summary>
public readonly record struct AccessCheck(AccessResult Result, string? Detail = null)
{
    public bool Ok => Result == AccessResult.Ok;
    public bool IsNotFound => Result == AccessResult.NotFound;
    public bool IsForbidden => Result == AccessResult.Forbidden;

    public static AccessCheck Allow() => new(AccessResult.Ok);
    public static AccessCheck Forbidden(string detail = "No tiene acceso a este cliente")
        => new(AccessResult.Forbidden, detail);
    public static AccessCheck NotFound(string detail) => new(AccessResult.NotFound, detail);
}
