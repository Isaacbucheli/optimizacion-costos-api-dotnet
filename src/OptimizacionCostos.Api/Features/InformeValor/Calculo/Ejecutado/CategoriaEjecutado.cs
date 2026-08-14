namespace OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;

/// <summary>Las categorías del gráfico apilado del acumulado (las cinco de la PPT de MERCANTIL
/// más el residual explícito de D1). Precedencia de <see cref="Resolver"/>: una reserva SIEMPRE
/// es "Reservas"; un hallazgo del barrido mapea por su check; si no hay mapeo, cae a la
/// categoría BITCOST del recurso; y lo que nada clasifica va al residual, visible, nunca
/// descartado.</summary>
public static class CategoriaEjecutado
{
    public const string Vms = "VMs (right-size / apagado)";
    public const string Reservas = "Reservas";
    public const string Discos = "Discos / Réplicas";
    public const string Red = "Red (IPs / Endpoints / DNS)";
    public const string AppService = "App Service";
    public const string Residual = "(sin categoría)";

    // Los 13 CheckId reales de OptimizationChecks.Registered (Features/Optimization/OptimizationChecks.cs),
    // asignados por lo que cada check detecta. Dos no calzan de forma literal en las cinco categorías de
    // la PPT y se anotan aparte:
    //   - storage_no_retention (cuentas de Storage sin retención/soft-delete de blobs) → Residual: es governance
    //     de storage accounts, no de discos administrados; declarado así en D1 para no forzarlo a un balde
    //     que no le corresponde. Si el negocio decide otra cosa, es una línea.
    //   - lb_appgw_no_backend, empty_subnets, basic_load_balancers, orphaned_nics → Red: son recursos de
    //     networking (load balancer, App Gateway, subnet, NIC) aunque el check no hable de IP/DNS/Endpoint
    //     en el nombre.
    //   - vms_without_ha, vms_without_ahb → Vms: quedan deliberadamente en VMs (tipo de recurso umbrella),
    //     no son sub-categorías de otra cosa.
    internal static readonly IReadOnlyDictionary<string, string> PorCheck = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["orphaned_disks"] = Discos,               // Discos administrados no conectados
        ["orphaned_public_ips"] = Red,              // Public IPs sin asociar
        ["stopped_not_deallocated_vms"] = Vms,      // VMs detenidas no desasignadas
        ["long_deallocated_vms"] = Vms,             // VMs desasignadas (posibles VMs olvidadas)
        ["empty_app_service_plans"] = AppService,   // App Service Plans sin aplicaciones
        ["lb_appgw_no_backend"] = Red,               // Balanceadores / App Gateways sin backend
        ["storage_no_retention"] = Residual,        // Storage sin política de retención (governance, no discos)
        ["orphaned_nics"] = Red,                     // Interfaces de red huérfanas
        ["empty_subnets"] = Red,                     // Subnets vacías
        ["vms_without_ha"] = Vms,                    // VMs sin alta disponibilidad
        ["basic_load_balancers"] = Red,              // Load Balancers Basic (en retiro)
        ["old_snapshots"] = Discos,                  // Snapshots antiguos
        ["vms_without_ahb"] = Vms,                   // Azure Hybrid Benefit no aplicado
    };

    public static string Resolver(string fuente, string? checkId, string? bitcostCategory)
    {
        if (fuente == "reserva") return Reservas;
        if (checkId is not null && PorCheck.TryGetValue(checkId, out var porCheck)) return porCheck;
        if (!string.IsNullOrWhiteSpace(bitcostCategory)) return bitcostCategory!;
        return Residual;
    }
}
