namespace OptimizacionCostos.Api.Features.CostEngine.Engine;

/// <summary>
/// Entrada del catálogo de servicios (dbo.service_catalog) que el motor necesita.
/// Port de los campos que usa cost_engine.py / service_catalog.py.
/// </summary>
public sealed record ServiceCatalogEntry
{
    public required string ServiceKey { get; init; }
    public string? DisplayName { get; init; }
    public required string AzureResourceType { get; init; }
    public string? ServiceCategory { get; init; }
    /// <summary>KQL de Resource Graph para importar este servicio (la usa el import, B4).</summary>
    public string? KqlQuery { get; init; }
    public string? DetailTableName { get; init; }
    public string? InserterKey { get; init; }
    public required string CalculatorKey { get; init; }
    public int? DisplayOrder { get; init; }
    public bool IsActive { get; init; }

    public bool IsInternal => ServiceDependencies.InternalServiceKeys.Contains(ServiceKey);
}
