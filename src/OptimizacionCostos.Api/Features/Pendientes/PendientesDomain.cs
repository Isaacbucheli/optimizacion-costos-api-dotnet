using OptimizacionCostos.Api.Auth;

namespace OptimizacionCostos.Api.Features.Pendientes;

/// <summary>
/// Las dos áreas del tablero. El valor viaja tal cual a las columnas <c>Area</c> de la BD
/// (<c>dbo.Cliente.Area</c>, <c>dbo.Pendiente.Area</c>), así que no se traduce ni se abrevia.
/// Cada área es un módulo de permisos aparte (decisión del usuario, 2026-07-28): alguien puede ver
/// Infra y no CDC.
/// </summary>
public static class PendientesArea
{
    public const string Cdc = "CDC";
    public const string Infra = "INFRA";

    /// <summary>
    /// Área de la ruta (case-insensitive) → valor de BD + clave de módulo. Null si no existe, y el
    /// controlador lo traduce a 400 (no a 403: un área inexistente no es un problema de permisos).
    /// </summary>
    public static (string Area, string ModuleKey)? Resolve(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (string.Equals(value, Cdc, StringComparison.OrdinalIgnoreCase)) return (Cdc, Modules.PendientesCdc);
        if (string.Equals(value, Infra, StringComparison.OrdinalIgnoreCase)) return (Infra, Modules.PendientesInfra);
        return null;
    }
}

/// <summary>
/// Valores de dominio EXACTOS tal como están hoy en la BD del tablero (auditados 2026-07-28).
/// Ojo con <c>EN_PROGRESO</c>: va con guion bajo aunque la UI muestre "En progreso". Escribir
/// "EN PROGRESO" crearía un cuarto estado que ninguno de los dos frentes filtra.
/// </summary>
public static class PendientesDomain
{
    public static readonly string[] Tipos = ["PENDIENTE", "BLOQUEANTE"];
    public static readonly string[] Prioridades = ["ALTA", "MEDIA", "BAJA"];
    public static readonly string[] Estados = ["ABIERTO", "EN_PROGRESO", "CERRADO"];
    public static readonly string[] Categorias = ["ALTO", "MEDIO", "BAJO"];

    public const string TipoDefault = "PENDIENTE";
    public const string PrioridadDefault = "MEDIA";
    public const string EstadoDefault = "ABIERTO";

    /// <summary>
    /// Normaliza a mayúsculas y convierte espacios en guion bajo ("en progreso" → "EN_PROGRESO").
    /// Devuelve null si el valor no está en la lista blanca; vacío/null también devuelve null.
    /// </summary>
    public static string? Normalize(string? raw, string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().ToUpperInvariant().Replace(' ', '_');
        return Array.Exists(allowed, a => a == value) ? value : null;
    }
}
