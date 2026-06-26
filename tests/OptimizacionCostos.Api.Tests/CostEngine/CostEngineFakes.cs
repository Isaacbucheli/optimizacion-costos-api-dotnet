using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.CostEngine.Engine;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>Catálogo en memoria.</summary>
public sealed class FakeServiceCatalog(IReadOnlyList<ServiceCatalogEntry> services) : IServiceCatalog
{
    public IReadOnlyList<ServiceCatalogEntry> ListServices(bool onlyActive = false)
        => onlyActive ? services.Where(s => s.IsActive).ToList() : services;
}

/// <summary>Loader en memoria: filas canned por service_key; registra llamadas de enriquecimiento.</summary>
public sealed class FakeResourceLoader : IResourceLoader
{
    public Dictionary<string, IReadOnlyList<ResourceRow>> Rows { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, SqlVmInfo> SqlVmInfo { get; } = new(StringComparer.Ordinal);
    public HashSet<string> StoppedVmNames { get; } = new(StringComparer.Ordinal);
    public bool LoadSqlVmInfoCalled { get; private set; }
    public bool LoadStoppedVmNamesCalled { get; private set; }

    public IReadOnlyList<ResourceRow> LoadResourcesForService(
        int analysisId, ServiceCatalogEntry service, int resourceOffset = 0, int? resourceLimit = null)
        => Rows.GetValueOrDefault(service.ServiceKey, []);

    public IReadOnlyDictionary<string, SqlVmInfo> LoadSqlVmInfo(int analysisId)
    {
        LoadSqlVmInfoCalled = true;
        return SqlVmInfo;
    }

    public IReadOnlySet<string> LoadStoppedVmNames(int analysisId)
    {
        LoadStoppedVmNamesCalled = true;
        return StoppedVmNames;
    }
}

/// <summary>Store en memoria: registra inserts (y el orden por servicio) y borrados.</summary>
public sealed class FakeCostResultStore : ICostResultStore
{
    public List<CostResult> Inserted { get; } = [];
    public List<string> InsertOrderByService { get; } = [];
    public int DeleteAllCount { get; private set; }
    public List<string> DeleteForServiceCalls { get; } = [];

    public void DeleteAllForAnalysis(int analysisId) => DeleteAllCount++;

    public void DeleteForService(int analysisId, string serviceKey) => DeleteForServiceCalls.Add(serviceKey);

    public void Insert(IReadOnlyList<CostResult> results)
    {
        if (results.Count > 0)
        {
            InsertOrderByService.Add(results[0].ServiceKey);
        }
        Inserted.AddRange(results);
    }
}
