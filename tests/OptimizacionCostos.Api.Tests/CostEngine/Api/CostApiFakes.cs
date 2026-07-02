using System.Security.Claims;
using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.CostEngine.Engine;
using OptimizacionCostos.Api.Features.CostEngine.Scenarios;
using OptimizacionCostos.Api.Features.FinOpsData;

namespace OptimizacionCostos.Api.Tests.CostEngine.Api;

/// <summary>
/// Doble de prueba de <see cref="IAnalysisAccess"/>. Modela el control de acceso por cliente
/// sin BD: admite admins globales, un mapa analysis_id -> client_id, y un set de clientes
/// accesibles por email. Replica las reglas de access_control.py.
/// </summary>
public sealed class FakeAnalysisAccess : IAnalysisAccess
{
    /// <summary>analysis_id -> client_id. Lo que no esté aquí se considera inexistente (404).</summary>
    public Dictionary<int, int> AnalysisToClient { get; } = new();

    /// <summary>cost_result_id -> analysis_id. Lo que no esté aquí no existe (404).</summary>
    public Dictionary<int, int> CostResultToAnalysis { get; } = new();

    /// <summary>email -> set de client_id asignados (para roles no admin).</summary>
    public Dictionary<string, HashSet<int>> Assignments { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlySet<int>?> AccessibleClientIdsAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (user.IsInRole("admin"))
            return Task.FromResult<IReadOnlySet<int>?>(null);
        var email = user.FindFirst("sub")?.Value ?? "";
        var ids = Assignments.TryGetValue(email, out var set) ? set : new HashSet<int>();
        return Task.FromResult<IReadOnlySet<int>?>(ids);
    }

    public async Task<AccessCheck> AssertAnalysisAccessAsync(ClaimsPrincipal user, int analysisId, CancellationToken ct = default)
    {
        if (!AnalysisToClient.TryGetValue(analysisId, out var clientId))
            return AccessCheck.NotFound("Analysis not found");
        return await AssertClientAsync(user, clientId);
    }

    public async Task<AccessCheck> AssertCostResultAccessAsync(ClaimsPrincipal user, int costResultId, CancellationToken ct = default)
    {
        if (!CostResultToAnalysis.TryGetValue(costResultId, out var analysisId))
            return AccessCheck.NotFound("cost_result not found");
        if (!AnalysisToClient.TryGetValue(analysisId, out var clientId))
            return AccessCheck.NotFound("Analysis not found");
        return await AssertClientAsync(user, clientId);
    }

    public Task<AccessCheck> AssertClientAccessAsync(ClaimsPrincipal user, int clientId, CancellationToken ct = default)
        => AssertClientAsync(user, clientId);

    /// <summary>file_id -> analysis_id. Lo que no esté aquí no existe (404).</summary>
    public Dictionary<int, int> FileToAnalysis { get; } = new();

    public async Task<AccessCheck> AssertFileAccessAsync(ClaimsPrincipal user, int fileId, CancellationToken ct = default)
    {
        if (!FileToAnalysis.TryGetValue(fileId, out var analysisId))
            return AccessCheck.NotFound("File not found");
        if (!AnalysisToClient.TryGetValue(analysisId, out var clientId))
            return AccessCheck.NotFound("File not found");
        return await AssertClientAsync(user, clientId);
    }

    private async Task<AccessCheck> AssertClientAsync(ClaimsPrincipal user, int clientId)
    {
        var ids = await AccessibleClientIdsAsync(user);
        if (ids is null) return AccessCheck.Allow(); // admin global
        return ids.Contains(clientId) ? AccessCheck.Allow() : AccessCheck.Forbidden();
    }
}

/// <summary>Doble de prueba de <see cref="ICostResultsQuery"/> con datos en memoria.</summary>
public sealed class FakeCostResultsQuery : ICostResultsQuery
{
    public Dictionary<int, List<CostResultRow>> ResultsByAnalysis { get; } = new();
    public Dictionary<int, List<ScenarioReadDto>> ScenariosByAnalysis { get; } = new();
    /// <summary>cost_result_id existentes (para distinguir 404 de UPDATE exitoso).</summary>
    public HashSet<int> ExistingCostResults { get; } = new();

    public List<(int CostResultId, double? Cost, string? Note)> SetManualCostCalls { get; } = new();

    public Task<IReadOnlyList<CostResultRow>> GetResultsAsync(int analysisId, string? serviceKey, CancellationToken ct = default)
    {
        var rows = ResultsByAnalysis.TryGetValue(analysisId, out var list) ? list : new();
        if (!string.IsNullOrEmpty(serviceKey))
            rows = rows.Where(r => r.ServiceKey == serviceKey).ToList();
        return Task.FromResult<IReadOnlyList<CostResultRow>>(rows);
    }

    public Task<IReadOnlyList<ScenarioReadDto>> GetScenariosAsync(int analysisId, CancellationToken ct = default)
    {
        var rows = ScenariosByAnalysis.TryGetValue(analysisId, out var list) ? list : new();
        return Task.FromResult<IReadOnlyList<ScenarioReadDto>>(rows);
    }

    public Task<bool> SetManualCostAsync(int costResultId, double? manualMonthlyCost, string? manualCostNote, CancellationToken ct = default)
    {
        SetManualCostCalls.Add((costResultId, manualMonthlyCost, manualCostNote));
        return Task.FromResult(ExistingCostResults.Contains(costResultId));
    }
}

/// <summary>Catalogo vacio: el motor responde "No services to calculate" sin tocar BD.</summary>
public sealed class FakeServiceCatalog : IServiceCatalog
{
    public IReadOnlyList<ServiceCatalogEntry> ListServices(bool onlyActive = false) => [];
}

/// <summary>Loader que nunca se invoca (catalogo vacio); presente solo para satisfacer el DI.</summary>
public sealed class FakeResourceLoader : IResourceLoader
{
    public IReadOnlyList<ResourceRow> LoadResourcesForService(
        int analysisId, ServiceCatalogEntry service, int resourceOffset = 0, int? resourceLimit = null) => [];
    public IReadOnlyDictionary<string, SqlVmInfo> LoadSqlVmInfo(int analysisId)
        => new Dictionary<string, SqlVmInfo>();
    public IReadOnlySet<string> LoadStoppedVmNames(int analysisId) => new HashSet<string>();
}

/// <summary>Store de resultados no-op (no persiste nada en los tests de acceso).</summary>
public sealed class FakeCostResultStore : ICostResultStore
{
    public void DeleteAllForAnalysis(int analysisId) { }
    public void DeleteForService(int analysisId, string serviceKey) { }
    public void Insert(IReadOnlyList<CostResult> results) { }
}

/// <summary>
/// Fuente de datos de escenarios en memoria. Por defecto sin resultados -> BuildScenarios
/// devuelve el error "No cost_results...". Configurable via <see cref="Results"/>.
/// </summary>
public sealed class FakeScenarioDataSource : IScenarioDataSource
{
    public List<ScenarioResultRow> Results { get; } = new();
    private int _scenarioSeq;

    public IReadOnlyList<ScenarioResultRow> LoadResultsWithMetadata(int analysisId) => Results;
    public void DeleteScenarios(int analysisId) { }
    public int InsertScenario(
        int analysisId, ScenarioConfig config,
        double totalMonthly, double totalAnnual,
        double savingsMonthly, double savingsAnnual, double savingsPct) => ++_scenarioSeq;
    public void InsertBreakdown(int scenarioId, ScenarioBreakdownLine line) { }
}

/// <summary>
/// Fake no-op de <see cref="IFinOpsDataRefreshService"/>: registrado en <see cref="CostApiTestFactory"/>
/// para que el hook best-effort de POST calculate no dispare HTTP/SQL real en los tests (el servicio
/// real se resuelve vía AddHttpClient en Program.cs). Solo cuenta invocaciones.
/// </summary>
public sealed class FakeFinOpsDataRefreshService : IFinOpsDataRefreshService
{
    public int EnsureFreshCalls { get; private set; }

    public Task<IReadOnlyList<FinOpsRefreshResult>> RefreshAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FinOpsRefreshResult>>([]);

    public Task<bool> EnsureFreshBestEffortAsync(TimeSpan timeout, CancellationToken ct)
    {
        EnsureFreshCalls++;
        return Task.FromResult(true);
    }
}
