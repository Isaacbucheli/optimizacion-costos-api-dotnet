using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.CostEngine.Engine;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using OptimizacionCostos.Api.Features.CostEngine.Scenarios;
using OptimizacionCostos.Api.Features.FinOpsData;
using OptimizacionCostos.Api.Tests.CostEngine;

namespace OptimizacionCostos.Api.Tests.CostEngine.Api;

/// <summary>
/// Levanta la API real en memoria para probar el router de costos. Reemplaza SOLO la BD:
/// IUserDirectory (auth), el control de acceso (IAnalysisAccess), las consultas
/// (ICostResultsQuery), el cache de precios (IPriceCache) y las dependencias del motor /
/// escenarios por fakes. El pipeline de auth/JWT/rutas/JSON es el de produccion, igual
/// que TestAppFactory de AlertCatalog.
/// </summary>
public sealed class CostApiTestFactory : WebApplicationFactory<Program>
{
    public const string Secret = TestAppFactory.Secret;

    public FakeUserDirectory Directory { get; } = new();
    public FakeAnalysisAccess Access { get; } = new();
    public FakeCostResultsQuery Query { get; } = new();
    public FakePriceCache PriceCache { get; } = new();
    public FakeScenarioDataSource ScenarioData { get; } = new();
    public FakeFinOpsDataRefreshService FinOpsRefresh { get; } = new();

    public CostApiTestFactory()
    {
        Environment.SetEnvironmentVariable("JWT_SECRET", Secret);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserDirectory>();
            services.AddSingleton<IUserDirectory>(Directory);

            // Fase 3: control de acceso y consultas.
            services.RemoveAll<IAnalysisAccess>();
            services.AddSingleton<IAnalysisAccess>(Access);
            services.RemoveAll<ICostResultsQuery>();
            services.AddSingleton<ICostResultsQuery>(Query);
            services.RemoveAll<IPriceCache>();
            services.AddSingleton<IPriceCache>(PriceCache);

            // Dependencias del motor / escenarios -> fakes sin BD. CostEngine y
            // ScenarioService siguen siendo las clases reales (resuelven con estos fakes).
            services.RemoveAll<IServiceCatalog>();
            services.AddSingleton<IServiceCatalog>(new FakeServiceCatalog());
            services.RemoveAll<IResourceLoader>();
            services.AddSingleton<IResourceLoader>(new FakeResourceLoader());
            services.RemoveAll<ICostResultStore>();
            services.AddSingleton<ICostResultStore>(new FakeCostResultStore());
            services.RemoveAll<IScenarioDataSource>();
            services.AddSingleton<IScenarioDataSource>(ScenarioData);

            // Fase FinOps: hook best-effort en calculate. Se reemplaza el servicio real
            // (AddHttpClient -> HTTP/SQL real) por un fake determinista que solo cuenta llamadas.
            services.RemoveAll<IFinOpsDataRefreshService>();
            services.AddSingleton<IFinOpsDataRefreshService>(FinOpsRefresh);
        });
    }
}
