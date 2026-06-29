using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.Reports;

// Port 1:1 de app/reports/azure_inventory.py. Los nombres JSON ([JsonPropertyName])
// REPRODUCEN EXACTO las claves de los dicts del Python porque las consume el builder y el front;
// no cambiar ninguna. Donde el Python devuelve dicts heterogéneos (resumen RBAC) se usa
// Dictionary<string, object?> para no perder forma.

/// <summary>Una VM del inventario vivo (port de fetch_vm_inventory). Claves del dict Python.</summary>
public sealed record VmInventoryItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("subscription")] string? Subscription,
    [property: JsonPropertyName("resource_group")] string ResourceGroup,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("location_raw")] string LocationRaw,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("size")] string Size,
    [property: JsonPropertyName("disks")] int Disks);

/// <summary>vCPU/RAM de un VM size (valor del mapa de fetch_vm_sizes). vcpu/ram_gb pueden venir null.</summary>
public sealed record VmSizeInfo(
    [property: JsonPropertyName("vcpu")] int? Vcpu,
    [property: JsonPropertyName("ram_gb")] double RamGb);

/// <summary>Fila de estado por tipo de servicio (port de fetch_service_status).</summary>
public sealed record ServiceStatusItem(
    [property: JsonPropertyName("tipo")] string Tipo,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("disponibles")] int Disponibles,
    [property: JsonPropertyName("con_alerta")] int ConAlerta,
    [property: JsonPropertyName("obs")] string Obs);

/// <summary>Item de respaldo protegido (port de la lista "items" de fetch_backups).</summary>
public sealed record BackupItem(
    [property: JsonPropertyName("servidor")] string? Servidor,
    [property: JsonPropertyName("tipo")] string Tipo,
    [property: JsonPropertyName("politica")] string Politica,
    [property: JsonPropertyName("frecuencia")] string Frecuencia,
    [property: JsonPropertyName("retencion")] string Retencion,
    [property: JsonPropertyName("estado")] string Estado,
    [property: JsonPropertyName("ultimo")] string Ultimo,
    [property: JsonPropertyName("subscription")] string? Subscription,
    [property: JsonPropertyName("resource_group")] string ResourceGroup);

/// <summary>Resumen de un Recovery Services Vault (port de la lista "vaults" de fetch_backups).</summary>
public sealed record BackupVault(
    [property: JsonPropertyName("vault")] string? Vault,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("elementos")] int Elementos,
    [property: JsonPropertyName("politicas")] string Politicas,
    [property: JsonPropertyName("ultimo")] string Ultimo);

/// <summary>Resultado de fetch_backups: vaults + items + conteo de jobs del mes por estado.</summary>
public sealed record BackupResult(
    [property: JsonPropertyName("vaults")] IReadOnlyList<BackupVault> Vaults,
    [property: JsonPropertyName("items")] IReadOnlyList<BackupItem> Items,
    [property: JsonPropertyName("jobs_mes")] IReadOnlyDictionary<string, int> JobsMes);

/// <summary>App Service Plan (port de fetch_app_service_plans). instancias/apps son string como en Python.</summary>
public sealed record AppServicePlanItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("plan")] string? Plan,
    [property: JsonPropertyName("sku")] string? Sku,
    [property: JsonPropertyName("instancias")] string? Instancias,
    [property: JsonPropertyName("apps")] string? Apps,
    [property: JsonPropertyName("estado")] string? Estado);

/// <summary>Fila de conectividad ER/VPN (port de fetch_connectivity).</summary>
public sealed record ConnectivityItem(
    [property: JsonPropertyName("nombre")] string? Nombre,
    [property: JsonPropertyName("proveedor")] string? Proveedor,
    [property: JsonPropertyName("ancho")] string? Ancho,
    [property: JsonPropertyName("estado")] string? Estado,
    [property: JsonPropertyName("obs")] string? Obs);

/// <summary>SQL Managed Instance (port de fetch_sql_mi_inventory).</summary>
public sealed record SqlMiItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("vcores")] int? Vcores,
    [property: JsonPropertyName("storage_gb")] int? StorageGb,
    [property: JsonPropertyName("bases")] int Bases,
    [property: JsonPropertyName("estado")] string? Estado);

/// <summary>Evento de Service Health del periodo (port de fetch_service_health_events).</summary>
public sealed record ServiceHealthEvent(
    [property: JsonPropertyName("inicio")] string Inicio,
    [property: JsonPropertyName("fin")] string Fin,
    [property: JsonPropertyName("servicios")] string Servicios,
    [property: JsonPropertyName("causa")] string Causa);
