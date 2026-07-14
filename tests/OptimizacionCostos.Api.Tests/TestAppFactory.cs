using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.AlertCatalog;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests;

/// <summary>
/// Levanta la API real en memoria reemplazando solo la BD (IUserDirectory e
/// IAlertCatalogStore) por fakes. El pipeline de auth/JWT/rutas/JSON es el de produccion.
/// </summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    public const string Secret = "test-secret-con-mas-de-32-caracteres-1234567890";

    public FakeUserDirectory Directory { get; } = new();
    public FakeAlertCatalogStore Store { get; } = new FakeAlertCatalogStore().Seed();

    public TestAppFactory()
    {
        // AppConfig lee la variable de entorno antes que la configuracion en memoria,
        // y se evalua al construir el host. Fijarla aqui garantiza el mismo secreto
        // que usa BitJwt y anula cualquier JWT_SECRET real de la maquina.
        Environment.SetEnvironmentVariable("JWT_SECRET", Secret);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserDirectory>();
            services.AddSingleton<IUserDirectory>(Directory);
            services.RemoveAll<IAlertCatalogStore>();
            services.AddSingleton<IAlertCatalogStore>(Store);
            services.RemoveAll<IModulePermissionStore>();
            services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
        });
    }
}
