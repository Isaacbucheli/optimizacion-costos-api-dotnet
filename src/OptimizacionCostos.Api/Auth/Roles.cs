namespace OptimizacionCostos.Api.Auth;

/// <summary>Roles validos (paridad con VALID_ROLES del FastAPI).</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Consultor = "consultor";
    public const string Lector = "lector";

    /// <summary>Roles que pueden mutar (crear/editar/eliminar). lector solo lee.</summary>
    public const string Editors = Admin + "," + Consultor;

    public static readonly HashSet<string> Valid = new(StringComparer.OrdinalIgnoreCase)
    {
        Admin, Consultor, Lector,
    };
}
