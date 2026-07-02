using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
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
        });
    }
}
