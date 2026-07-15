using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests.Auth;

/// <summary>
/// Blindaje del superadministrador (SUPERADMIN_EMAILS): no lo puede eliminar/editar OTRO usuario;
/// él mismo no puede degradarse ni desactivarse; el login lo auto-repara si fue alterado en BD;
/// /auth/me y la lista exponen is_super_admin. Pipeline MVC real, BD fake.
/// </summary>
public sealed class SuperAdminProtectionTests : IClassFixture<SuperAdminProtectionTests.Factory>
{
    private const string SuperEmail = "super@bit-e2e.local";
    private readonly Factory _factory;
    // Fixture compartido → limpio el store antes de cada test para no arrastrar el superadmin sembrado.
    public SuperAdminProtectionTests(Factory factory) { _factory = factory; _factory.Users.Clear(); }

    private HttpClient ClientFor(string email, string role)
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BitJwt.Create(Factory.Secret, email, "Test User", role));
        return client;
    }

    private int SeedSuper() => _factory.Users.AddUser(SuperEmail, "Password123", "admin", mustChange: false).UserId;

    [Fact]
    public async Task Otro_admin_no_puede_eliminar_superadmin()
    {
        var id = SeedSuper();
        var res = await ClientFor("otro-admin@bit.ec", Roles.Admin).DeleteAsync($"/auth/users/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.NotNull(_factory.Users.Find(SuperEmail)); // sigue existiendo
    }

    [Fact]
    public async Task Otro_admin_no_puede_degradar_superadmin()
    {
        var id = SeedSuper();
        var res = await ClientFor("otro-admin2@bit.ec", Roles.Admin)
            .PutAsJsonAsync($"/auth/users/{id}", new { role = "lector" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("admin", _factory.Users.Find(SuperEmail)!.Role); // rol intacto
    }

    [Fact]
    public async Task Superadmin_no_puede_autodegradarse()
    {
        var id = SeedSuper();
        var res = await ClientFor(SuperEmail, Roles.Admin)
            .PutAsJsonAsync($"/auth/users/{id}", new { role = "lector" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Superadmin_no_puede_autodesactivarse()
    {
        var id = SeedSuper();
        var res = await ClientFor(SuperEmail, Roles.Admin)
            .PutAsJsonAsync($"/auth/users/{id}", new { is_active = false });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Superadmin_puede_cambiar_su_propio_nombre()
    {
        var id = SeedSuper();
        var res = await ClientFor(SuperEmail, Roles.Admin)
            .PutAsJsonAsync($"/auth/users/{id}", new { full_name = "Nombre Nuevo" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Nombre Nuevo", _factory.Users.Find(SuperEmail)!.FullName);
    }

    [Fact]
    public async Task Login_reactiva_y_repromueve_al_superadmin_alterado()
    {
        SeedSuper();
        // Simula una alteración por BD directa: degradado y desactivado.
        var u = _factory.Users.Find(SuperEmail)!;
        u.Role = "lector";
        u.IsActive = false;

        var res = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/login", new { username = SuperEmail, password = "Password123" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // reactivado por la reconciliación
        Assert.True(u.IsActive);
        Assert.Equal("admin", u.Role);
    }

    [Fact]
    public async Task Lista_marca_is_super_admin_solo_en_el_superadmin()
    {
        SeedSuper();
        _factory.Users.AddUser("normalito@bit.ec", "Password123", "consultor", mustChange: false);
        var items = await ClientFor("admin-list@bit.ec", Roles.Admin).GetFromJsonAsync<JsonElement>("/auth/users");
        var super = items.EnumerateArray().Single(u => u.GetProperty("email").GetString() == SuperEmail);
        var normal = items.EnumerateArray().Single(u => u.GetProperty("email").GetString() == "normalito@bit.ec");
        Assert.True(super.GetProperty("is_super_admin").GetBoolean());
        Assert.False(normal.GetProperty("is_super_admin").GetBoolean());
    }

    [Fact]
    public async Task Me_del_superadmin_expone_is_super_admin()
    {
        SeedSuper();
        var me = await ClientFor(SuperEmail, Roles.Admin).GetFromJsonAsync<JsonElement>("/auth/me");
        Assert.True(me.GetProperty("is_super_admin").GetBoolean());
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeAppUserStore Users { get; } = new();

        public Factory()
        {
            Environment.SetEnvironmentVariable("JWT_SECRET", Secret);
            Environment.SetEnvironmentVariable("SUPERADMIN_EMAILS", SuperEmail);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IAppUserStore>();
                services.AddSingleton<IAppUserStore>(Users);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
            });
        }
    }
}
