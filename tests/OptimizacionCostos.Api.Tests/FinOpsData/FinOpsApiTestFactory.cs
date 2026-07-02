using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.FinOpsData;

namespace OptimizacionCostos.Api.Tests.FinOpsData;

/// <summary>Fake de IFinOpsDataRefreshService: devuelve resultados enlatados y cuenta llamadas.</summary>
public sealed class FakeFinOpsDataRefreshService : IFinOpsDataRefreshService
{
    public int RefreshAllCalls { get; private set; }

    public IReadOnlyList<FinOpsRefreshResult> Results { get; set; } =
    [
        new FinOpsRefreshResult("pricing_units", "ok", 300),
        new FinOpsRefreshResult("regions", "ok", 400),
        new FinOpsRefreshResult("services", "ok", 300),
        new FinOpsRefreshResult("resource_types", "ok", 1500),
        new FinOpsRefreshResult("commitment_eligibility", "ok", 120_000),
    ];

    public Task<IReadOnlyList<FinOpsRefreshResult>> RefreshAllAsync(CancellationToken ct)
    {
        RefreshAllCalls++;
        return Task.FromResult(Results);
    }

    public Task<bool> EnsureFreshBestEffortAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
}

/// <summary>
/// Doble de prueba de <see cref="IAnalysisAccess"/> para el router /finops-data. Modela el
/// control de acceso por cliente sin BD (mismo patrón que FakeAnalysisAccess de CostEngine).
/// </summary>
public sealed class FakeAnalysisAccess : IAnalysisAccess
{
    /// <summary>analysis_id -> client_id. Lo que no esté aquí se considera inexistente (404).</summary>
    public Dictionary<int, int> AnalysisToClient { get; } = new();

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

    public Task<AccessCheck> AssertCostResultAccessAsync(ClaimsPrincipal user, int costResultId, CancellationToken ct = default)
        => Task.FromResult(AccessCheck.NotFound("cost_result not found"));

    public Task<AccessCheck> AssertClientAccessAsync(ClaimsPrincipal user, int clientId, CancellationToken ct = default)
        => AssertClientAsync(user, clientId);

    public Task<AccessCheck> AssertFileAccessAsync(ClaimsPrincipal user, int fileId, CancellationToken ct = default)
        => Task.FromResult(AccessCheck.NotFound("File not found"));

    private async Task<AccessCheck> AssertClientAsync(ClaimsPrincipal user, int clientId)
    {
        var ids = await AccessibleClientIdsAsync(user);
        if (ids is null) return AccessCheck.Allow(); // admin global
        return ids.Contains(clientId) ? AccessCheck.Allow() : AccessCheck.Forbidden();
    }
}

/// <summary>Doble de prueba de <see cref="IRiDiagnosticsQuery"/> con filas en memoria por análisis.</summary>
public sealed class FakeRiDiagnosticsQuery : IRiDiagnosticsQuery
{
    public Dictionary<int, List<RiDiagnosticRow>> Rows { get; } = new();

    public Task<IReadOnlyList<RiDiagnosticRow>> ListAsync(int analysisId, CancellationToken ct)
    {
        var rows = Rows.TryGetValue(analysisId, out var list) ? list : new List<RiDiagnosticRow>();
        return Task.FromResult<IReadOnlyList<RiDiagnosticRow>>(rows);
    }
}

/// <summary>Doble de prueba de <see cref="ICoverageQuery"/> con resultados enlatados por análisis.</summary>
public sealed class FakeCoverageQuery : ICoverageQuery
{
    public Dictionary<int, CoverageResult> Results { get; } = new();

    public Task<CoverageResult> GetAsync(int analysisId, CancellationToken ct)
    {
        var result = Results.TryGetValue(analysisId, out var r)
            ? r
            : new CoverageResult(0, 0, 0.0, new List<CoverageGap>());
        return Task.FromResult(result);
    }
}

/// <summary>
/// Levanta la API real en memoria para probar el router /finops-data. Reemplaza SOLO la BD/HTTP:
/// IUserDirectory (auth), IFinOpsDataRefreshService, IFinOpsDataStore e IFinOpsRefData por fakes.
/// El pipeline de auth/JWT/rutas/JSON (incl. IMemoryCache real) es el de producción, igual que
/// TestAppFactory de AlertCatalog.
/// </summary>
public sealed class FinOpsApiTestFactory : WebApplicationFactory<Program>
{
    public const string Secret = Tests.TestAppFactory.Secret;

    public FakeUserDirectory Directory { get; } = new();
    public FakeFinOpsDataRefreshService Refresh { get; } = new();
    public FakeFinOpsDataStore Store { get; } = new();
    public FakeFinOpsRefData RefData { get; } = new();
    public FakeAnalysisAccess Access { get; } = new();
    public FakeRiDiagnosticsQuery Diagnostics { get; } = new();
    public FakeCoverageQuery Coverage { get; } = new();

    public FinOpsApiTestFactory()
    {
        Environment.SetEnvironmentVariable("JWT_SECRET", Secret);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserDirectory>();
            services.AddSingleton<IUserDirectory>(Directory);

            services.RemoveAll<IFinOpsDataRefreshService>();
            services.AddSingleton<IFinOpsDataRefreshService>(Refresh);
            services.RemoveAll<IFinOpsDataStore>();
            services.AddSingleton<IFinOpsDataStore>(Store);
            services.RemoveAll<IFinOpsRefData>();
            services.AddSingleton<IFinOpsRefData>(RefData);

            services.RemoveAll<IAnalysisAccess>();
            services.AddSingleton<IAnalysisAccess>(Access);
            services.RemoveAll<IRiDiagnosticsQuery>();
            services.AddSingleton<IRiDiagnosticsQuery>(Diagnostics);
            services.RemoveAll<ICoverageQuery>();
            services.AddSingleton<ICoverageQuery>(Coverage);
        });
    }
}
