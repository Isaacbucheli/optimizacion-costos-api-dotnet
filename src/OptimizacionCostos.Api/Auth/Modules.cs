namespace OptimizacionCostos.Api.Auth;

/// <summary>Un módulo navegable de la plataforma (espejo de las secciones del front React).</summary>
public sealed record ModuleInfo(string Key, string Label, string Group);

/// <summary>
/// Catálogo de módulos gobernados por la matriz rol×módulo (dbo.role_module_permission).
/// Única fuente de verdad: un módulo nuevo se agrega AQUÍ y aparece en la pantalla de
/// permisos denegado por defecto (fila ausente = sin acceso). Las claves coinciden con
/// las secciones del front (AppShell.tsx / modules.ts). Clientes, Usuarios y Validación
/// inteligente NO entran: siguen siendo admin-only por [Authorize(Roles = Roles.Admin)].
/// </summary>
public static class Modules
{
    public const string Costos = "costos";
    public const string Optimization = "optimization";
    public const string ServiceCatalog = "service-catalog";
    public const string Waf = "waf";
    public const string WafIngestions = "waf-ingestions";
    public const string WafCost = "waf-cost";
    public const string Report = "report";
    public const string Reservations = "reservations";
    public const string Alerts = "alerts";
    public const string Policies = "policies";
    public const string Consultants = "consultants";
    public const string AccessReview = "access-review";

    public static readonly IReadOnlyList<ModuleInfo> All =
    [
        new(Costos, "Optimización de costos", "Matriz costos Azure"),
        new(Optimization, "Oportunidades de Optimización", "Matriz costos Azure"),
        new(ServiceCatalog, "Catálogo de servicios", "Matriz costos Azure"),
        new(Waf, "Recomendaciones", "Matriz mejoras Azure"),
        new(WafIngestions, "Historial de ingestas", "Matriz mejoras Azure"),
        new(WafCost, "Costo referencial Azure", "Matriz mejoras Azure"),
        new(Report, "Informe de gestión mensual", "Informes"),
        new(Reservations, "Reservas por vencer", "Gestión CDC"),
        new(Alerts, "Catálogo de alertas", "Gestión CDC"),
        new(Policies, "Catálogo de políticas", "Gestión CDC"),
        new(Consultants, "Asignación de consultores", "Gestión CDC"),
        new(AccessReview, "Revisión de accesos", "Gestión CDC"),
    ];

    public static readonly HashSet<string> ValidKeys =
        new(All.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
}
