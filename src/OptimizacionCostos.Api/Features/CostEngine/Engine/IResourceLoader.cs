namespace OptimizacionCostos.Api.Features.CostEngine.Engine;

/// <summary>Info de SQL VM asociada a una VM (para enriquecer el cálculo de compute).</summary>
public sealed record SqlVmInfo(string? SqlImageSku, string? SqlLicenseType, string? SqlImageOffer);

/// <summary>
/// Carga recursos del inventario para costear. Equivale a las funciones de carga y
/// enriquecimiento de cost_engine.py (_load_resources_for_service, _enrich_vms_with_sql_info,
/// _enrich_disks_with_stopped_vms).
/// </summary>
public interface IResourceLoader
{
    /// <summary>JOIN azure_resources + tabla detalle del servicio (paginable). Filas como ResourceRow.</summary>
    IReadOnlyList<ResourceRow> LoadResourcesForService(
        int analysisId, ServiceCatalogEntry service, int resourceOffset = 0, int? resourceLimit = null);

    /// <summary>Mapa azure_resource_id (lower) → SqlVmInfo de los SQL VMs del análisis.</summary>
    IReadOnlyDictionary<string, SqlVmInfo> LoadSqlVmInfo(int analysisId);

    /// <summary>Nombres (lower) de VMs apagadas (power_state sin "running").</summary>
    IReadOnlySet<string> LoadStoppedVmNames(int analysisId);
}
