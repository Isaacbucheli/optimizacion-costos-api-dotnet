namespace OptimizacionCostos.Api.Features.FinOpsData;

public sealed record FinOpsResourceTypeInfo(string DisplayName, string? ServiceCategory);

public interface IFinOpsRefData
{
    /// <summary>meterId → ri_eligible. Meter ausente del dict = unknown (whitelist).</summary>
    Task<IReadOnlyDictionary<string, bool>> GetRiEligibilityAsync(IReadOnlyCollection<string> meterIds, CancellationToken ct = default);
    /// <summary>region_id (lowercase) → nombre amigable ("eastus2" → "East US 2").</summary>
    Task<IReadOnlyDictionary<string, string>> GetRegionNamesAsync(CancellationToken ct = default);
    /// <summary>resource_type ARM (lowercase) → display name + categoría FOCUS.</summary>
    Task<IReadOnlyDictionary<string, FinOpsResourceTypeInfo>> GetResourceTypesAsync(CancellationToken ct = default);
}
