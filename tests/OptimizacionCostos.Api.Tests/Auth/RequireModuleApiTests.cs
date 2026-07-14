using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.AlertCatalog;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests.Auth;

/// <summary>
/// Gating por módulo sobre /alert-catalog (módulo "alerts"): pipeline MVC real,
/// solo BD fake. Cubre bypass admin, matriz por rol y candado lector-no-edita.
/// </summary>
public sealed class RequireModuleApiTests : IClassFixture<RequireModuleApiTests.Factory>
{
    private readonly Factory _factory;
    public RequireModuleApiTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string email, string role)
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        var token = BitJwt.Create(Factory.Secret, email, "Test User", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Admin_pasa_aunque_la_matriz_niegue_todo()
    {
        _factory.Perms.Set(Roles.Admin, Modules.Alerts, canView: false, canEdit: false); // se ignora
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var res = await client.GetAsync("/alert-catalog");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        _factory.Service.Invalidate();
    }

    [Fact]
    public async Task Consultor_sin_view_recibe_403_al_listar()
    {
        _factory.Perms.Set(Roles.Consultor, Modules.Alerts, canView: false, canEdit: false);
        _factory.Service.Invalidate();
        var client = ClientFor("c1@bit.ec", Roles.Consultor);
        var res = await client.GetAsync("/alert-catalog");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Consultor_con_view_sin_edit_lista_pero_no_crea()
    {
        _factory.Perms.Set(Roles.Consultor, Modules.Alerts, canView: true, canEdit: false);
        _factory.Service.Invalidate();
        var client = ClientFor("c2@bit.ec", Roles.Consultor);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alert-catalog")).StatusCode);
        var res = await client.PostAsJsonAsync("/alert-catalog", new { name = "Alerta X" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Consultor_con_edit_puede_crear()
    {
        _factory.Perms.Set(Roles.Consultor, Modules.Alerts, canView: true, canEdit: true);
        _factory.Service.Invalidate();
        var client = ClientFor("c3@bit.ec", Roles.Consultor);
        var res = await client.PostAsJsonAsync("/alert-catalog", new { name = "Alerta Y" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Lector_no_edita_aunque_la_matriz_diga_que_si()
    {
        _factory.Perms.Set(Roles.Lector, Modules.Alerts, canView: true, canEdit: true); // candado duro
        _factory.Service.Invalidate();
        var client = ClientFor("l1@bit.ec", Roles.Lector);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alert-catalog")).StatusCode);
        var res = await client.PostAsJsonAsync("/alert-catalog", new { name = "Alerta Z" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeAlertCatalogStore Store { get; } = new FakeAlertCatalogStore().Seed();
        public FakeModulePermissionStore Perms { get; } = new FakeModulePermissionStore().SeedDefaults();
        public IModulePermissionService Service => Services.GetRequiredService<IModulePermissionService>();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IAlertCatalogStore>();
                services.AddSingleton<IAlertCatalogStore>(Store);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(Perms);
                // El servicio cacheado debe ser singleton en tests para poder invalidarlo
                // desde fuera del scope del request.
                services.RemoveAll<IModulePermissionService>();
                services.AddSingleton<IModulePermissionService, ModulePermissionService>();
            });
        }
    }
}
