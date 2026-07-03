using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Cdc;
using OptimizacionCostos.Api.Tests.CostEngine.Api; // FakeAnalysisAccess

namespace OptimizacionCostos.Api.Tests.Cdc.Api;

/// <summary>
/// Levanta la API real en memoria para probar AnalysisRefreshController. Reemplaza la BD/Azure:
/// IUserDirectory (auth), IAnalysisAccess (acceso), IPowerHistoryService/IRiCoverageService (Azure),
/// IPowerHistoryJobQueue e IPowerHistoryJobStore por fakes. Auth/JWT/rutas/JSON son de producción.
/// </summary>
public sealed class CdcApiTestFactory : WebApplicationFactory<Program>
{
    public const string Secret = TestAppFactory.Secret;

    public FakeUserDirectory Directory { get; } = new();
    public FakeAnalysisAccess Access { get; } = new();
    public FakePowerHistoryService PowerHistory { get; } = new();
    public FakePowerHistoryJobStore Store { get; } = new();
    public FakePowerHistoryJobQueue Queue { get; } = new();

    public CdcApiTestFactory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserDirectory>();
            services.AddSingleton<IUserDirectory>(Directory);
            services.RemoveAll<IAnalysisAccess>();
            services.AddSingleton<IAnalysisAccess>(Access);
            services.RemoveAll<IPowerHistoryService>();
            services.AddSingleton<IPowerHistoryService>(PowerHistory);
            services.RemoveAll<IRiCoverageService>();
            services.AddSingleton<IRiCoverageService>(new FakeRiCoverageService());
            services.RemoveAll<IPowerHistoryJobStore>();
            services.AddSingleton<IPowerHistoryJobStore>(Store);
            services.RemoveAll<IPowerHistoryJobQueue>();
            services.AddSingleton<IPowerHistoryJobQueue>(Queue);
        });
    }
}
