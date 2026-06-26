namespace OptimizacionCostos.Api.Features.CostEngine.Engine;

/// <summary>Lee el catálogo de servicios. Equivale a service_catalog.list_services.</summary>
public interface IServiceCatalog
{
    IReadOnlyList<ServiceCatalogEntry> ListServices(bool onlyActive = false);
}
