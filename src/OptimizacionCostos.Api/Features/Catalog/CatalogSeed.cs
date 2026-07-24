namespace OptimizacionCostos.Api.Features.Catalog;

/// <summary>
/// Seed idempotente del catálogo de servicios (spec 2026-07-24). El catálogo original lo
/// sembraba la app Python (eliminada); las filas nuevas del stack .NET se siembran aquí:
/// INSERT-si-no-existe al arrancar, para que -valida y producción queden consistentes sin
/// pasos manuales. NO actualiza filas existentes (el catálogo es editable por el usuario).
/// </summary>
public static class CatalogSeed
{
    private const string SnapshotsKql = """
        Resources
        | where type =~ 'microsoft.compute/snapshots'
        | extend skuName = tostring(sku.name),
                 diskSizeGB = toint(properties.diskSizeGB),
                 incremental = tobool(properties.incremental),
                 timeCreated = tostring(properties.timeCreated)
        | project id, name, type, location, resourceGroup, subscriptionId,
                  skuName, diskSizeGB, incremental, timeCreated, properties
        """;

    private const string StorageFilesKql = """
        Resources
        | where type =~ 'microsoft.storage/storageaccounts'
        | where kind in~ ('StorageV2','Storage','FileStorage')
        | extend skuName = tostring(sku.name), kind = tostring(kind)
        | project id, name, type, location, resourceGroup, subscriptionId,
                  kind, skuName, properties
        """;

    /// <summary>
    /// Las 2 filas nuevas. display_order 90/91: DESPUÉS de todos los servicios existentes.
    /// Verificado contra la BD real (2026-07-24): los 12 servicios sembrados usan 10..80
    /// (public_ip = 80 es el máximo; mysql ya ocupa el 50 que asumía el plan), así que 50/51
    /// habría intercalado los servicios nuevos en medio de la matriz.
    /// </summary>
    public static readonly CatalogEntryWrite[] Entries =
    [
        new(
            ServiceKey: "snapshots",
            DisplayName: "Snapshots de discos",
            AzureResourceType: "microsoft.compute/snapshots",
            ServiceCategory: "Storage",
            DetailTableName: "snapshot_details",
            InserterKey: "snapshot",
            CalculatorKey: "snapshot",
            KqlQuery: SnapshotsKql,
            RiApplicable: false,
            RiFilterField: null, RiFilterValues: null, RiExcludeValues: null,
            AhbApplicable: false,
            RequiresManualCost: false,
            ExcelSheetName: "Snapshots",
            DisplayOrder: 90,
            IsActive: true,
            Notes: "Costeo referencial por tamaño del disco de origen (Azure factura por GB ocupado). Sembrado por CatalogSeed (spec 2026-07-24)."),
        new(
            ServiceKey: "storage_files",
            DisplayName: "Storage (Azure Files)",
            AzureResourceType: "microsoft.storage/storageaccounts",
            ServiceCategory: "Storage",
            DetailTableName: "storage_files_details",
            InserterKey: "storage_files",
            CalculatorKey: "storage_files",
            KqlQuery: StorageFilesKql,
            RiApplicable: true,
            RiFilterField: null, RiFilterValues: null, RiExcludeValues: null,
            AhbApplicable: false,
            RequiresManualCost: false,
            ExcelSheetName: "Storage Files",
            DisplayOrder: 91,
            IsActive: true,
            Notes: "Solo storage accounts con capacidad facturable de Azure Files > 10 TiB (10,240 GiB); corte aplicado en la importación (StorageFilesEnricher). Sembrado por CatalogSeed (spec 2026-07-24)."),
    ];

    public static async Task EnsureAsync(IServiceCatalogAdmin admin, ILogger logger, CancellationToken ct = default)
    {
        foreach (var entry in Entries)
        {
            if (await admin.ExistsAsync(entry.ServiceKey, ct))
            {
                continue;
            }
            await admin.CreateAsync(entry, ct);
            logger.LogInformation("CatalogSeed: servicio {ServiceKey} sembrado en service_catalog", entry.ServiceKey);
        }
    }
}
