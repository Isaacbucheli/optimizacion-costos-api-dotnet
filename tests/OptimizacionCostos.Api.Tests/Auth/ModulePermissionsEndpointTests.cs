using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests.Auth;

public sealed class ModulePermissionsEndpointTests : IClassFixture<ModulePermissionsEndpointTests.Factory>
{
    private readonly Factory _factory;
    public ModulePermissionsEndpointTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string email, string role)
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BitJwt.Create(Factory.Secret, email, "Test User", role));
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Me_de_admin_trae_los_12_modulos_todo_true()
    {
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var me = await client.GetFromJsonAsync<JsonElement>("/auth/me");
        var modules = me.GetProperty("modules");
        Assert.Equal(12, modules.GetArrayLength());
        Assert.All(modules.EnumerateArray(), m =>
        {
            Assert.True(m.GetProperty("can_view").GetBoolean());
            Assert.True(m.GetProperty("can_edit").GetBoolean());
        });
    }

    [Fact]
    public async Task Me_de_consultor_refleja_la_matriz()
    {
        _factory.Perms.Set(Roles.Consultor, Modules.Alerts, canView: false, canEdit: false);
        _factory.Service.Invalidate();
        var client = ClientFor("c@bit.ec", Roles.Consultor);
        var me = await client.GetFromJsonAsync<JsonElement>("/auth/me");
        var alerts = me.GetProperty("modules").EnumerateArray()
            .Single(m => m.GetProperty("key").GetString() == Modules.Alerts);
        Assert.False(alerts.GetProperty("can_view").GetBoolean());
        // restaurar para otros tests
        _factory.Perms.Set(Roles.Consultor, Modules.Alerts, canView: true, canEdit: true);
        _factory.Service.Invalidate();
    }

    [Fact]
    public async Task Get_matriz_es_solo_admin()
    {
        var client = ClientFor("c2@bit.ec", Roles.Consultor);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/auth/module-permissions")).StatusCode);
    }

    [Fact]
    public async Task Get_matriz_devuelve_catalogo_y_permisos_de_ambos_roles()
    {
        var client = ClientFor("admin2@bit.ec", Roles.Admin);
        var body = await client.GetFromJsonAsync<JsonElement>("/auth/module-permissions");
        Assert.Equal(12, body.GetProperty("modules").GetArrayLength());
        Assert.Equal(12, body.GetProperty("permissions").GetProperty("consultor").GetArrayLength());
        Assert.Equal(12, body.GetProperty("permissions").GetProperty("lector").GetArrayLength());
        var first = body.GetProperty("modules")[0];
        Assert.True(first.TryGetProperty("label", out _));
        Assert.True(first.TryGetProperty("group", out _));
    }

    [Fact]
    public async Task Put_valida_module_key_desconocido()
    {
        var client = ClientFor("admin3@bit.ec", Roles.Admin);
        var res = await client.PutAsync("/auth/module-permissions",
            Json("""{"permissions":{"consultor":[{"module_key":"inventada","can_view":true,"can_edit":false}]}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_valida_module_key_duplicado()
    {
        var client = ClientFor("admin3b@bit.ec", Roles.Admin);
        var res = await client.PutAsync("/auth/module-permissions", Json("""
            {"permissions":{"consultor":[
              {"module_key":"alerts","can_view":true,"can_edit":false},
              {"module_key":"ALERTS","can_view":true,"can_edit":true}
            ]}}
            """));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_valida_rol_desconocido()
    {
        var client = ClientFor("admin4@bit.ec", Roles.Admin);
        var res = await client.PutAsync("/auth/module-permissions",
            Json("""{"permissions":{"superuser":[{"module_key":"alerts","can_view":true,"can_edit":false}]}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_fuerza_lector_sin_edit_y_edit_implica_view()
    {
        var client = ClientFor("admin5@bit.ec", Roles.Admin);
        var res = await client.PutAsync("/auth/module-permissions", Json("""
            {"permissions":{
              "lector":[{"module_key":"alerts","can_view":true,"can_edit":true}],
              "consultor":[{"module_key":"alerts","can_view":false,"can_edit":true}]
            }}
            """));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var lector = body.GetProperty("permissions").GetProperty("lector").EnumerateArray()
            .Single(r => r.GetProperty("module_key").GetString() == "alerts");
        Assert.False(lector.GetProperty("can_edit").GetBoolean()); // candado
        var consultor = body.GetProperty("permissions").GetProperty("consultor").EnumerateArray()
            .Single(r => r.GetProperty("module_key").GetString() == "alerts");
        Assert.True(consultor.GetProperty("can_view").GetBoolean()); // edit ⇒ view
        _factory.Service.Invalidate();
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeModulePermissionStore Perms { get; } = new FakeModulePermissionStore().SeedDefaults();
        public IModulePermissionService Service => Services.GetRequiredService<IModulePermissionService>();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                // Sin fake de IAppUserStore, /auth/me llamaría al store SQL real (sin BD en
                // los tests) y 500-earía antes de llegar al fallback de claims. Un
                // FakeAppUserStore vacío (ya usado en TemporaryPasswordApiTests, mismo
                // namespace) devuelve null para cualquier email y fuerza ese fallback,
                // que es el comportamiento documentado que este test ejercita.
                services.RemoveAll<IAppUserStore>();
                services.AddSingleton<IAppUserStore>(new FakeAppUserStore());
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(Perms);
                services.RemoveAll<IModulePermissionService>();
                services.AddSingleton<IModulePermissionService, ModulePermissionService>();
            });
        }
    }
}
