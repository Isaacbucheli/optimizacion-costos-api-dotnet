namespace OptimizacionCostos.Api.Auth;

/// <summary>Roles validos (paridad con VALID_ROLES del FastAPI).</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Consultor = "consultor";
    public const string Lector = "lector";
    public const string Monitoreo = "monitoreo";

    /// <summary>Roles que pueden mutar (crear/editar/eliminar). lector y monitoreo solo leen.</summary>
    public const string Editors = Admin + "," + Consultor;

    public static readonly HashSet<string> Valid = new(StringComparer.OrdinalIgnoreCase)
    {
        Admin, Consultor, Lector, Monitoreo,
    };

    /// <summary>Roles de solo lectura: jamás editan, aunque la matriz diga lo contrario.</summary>
    public static bool IsReadOnly(string role) =>
        string.Equals(role, Lector, StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, Monitoreo, StringComparison.OrdinalIgnoreCase);
}
