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
/// WEB-12: el cierre de sesión revoca server-side. POST /auth/logout marca
/// tokens_revoked_at del usuario; change-password y el reset por admin también revocan
/// (cambio de credenciales = las sesiones anteriores mueren). change-password además
/// devuelve un token nuevo para que el usuario que acaba de cambiar su contraseña
/// temporal no quede expulsado.
/// </summary>
public sealed class LogoutApiTests : IClassFixture<LogoutApiTests.Factory>
{
    private readonly Factory _factory;
    public LogoutApiTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string email, string role)
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        var token = BitJwt.Create(Factory.Secret, email, "Test User", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Logout_marca_la_revocacion_y_responde_200()
    {
        _factory.Users.AddUser("qa.logout@bit.test", "Password123!", "consultor", mustChange: false);
        var client = ClientFor("qa.logout@bit.test", "consultor");

        var res = await client.PostAsync("/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("logged_out").GetBoolean());
        Assert.NotNull(_factory.Users.Find("qa.logout@bit.test")!.TokensRevokedAt);
    }

    [Fact]
    public async Task Logout_es_idempotente()
    {
        _factory.Users.AddUser("qa.doble@bit.test", "Password123!", "consultor", mustChange: false);
        var client = ClientFor("qa.doble@bit.test", "consultor");

        var primero = await client.PostAsync("/auth/logout", null);
        var segundo = await client.PostAsync("/auth/logout", null);

        Assert.Equal(HttpStatusCode.OK, primero.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segundo.StatusCode);
    }

    [Fact]
    public async Task Change_password_revoca_y_devuelve_token_nuevo()
    {
        _factory.Users.AddUser("qa.cambio@bit.test", "Password123!", "consultor", mustChange: false);
        var client = ClientFor("qa.cambio@bit.test", "consultor");

        var res = await client.PostAsJsonAsync("/auth/change-password",
            new { current_password = "Password123!", new_password = "NuevaClave456!" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("changed").GetBoolean());
        Assert.False(body.GetProperty("must_change_password").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("access_token").GetString())); // reemplazo inmediato
        Assert.NotNull(_factory.Users.Find("qa.cambio@bit.test")!.TokensRevokedAt);
    }

    [Fact]
    public async Task Reset_de_password_por_admin_revoca_las_sesiones_del_usuario()
    {
        var target = _factory.Users.AddUser("qa.reseteado@bit.test", "Password123!", "consultor", mustChange: false);
        var admin = ClientFor("qa.admin.reset@bit.test", "admin");

        var res = await admin.PutAsJsonAsync($"/auth/users/{target.UserId}",
            new { password = "OtraClave789!" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotNull(_factory.Users.Find("qa.reseteado@bit.test")!.TokensRevokedAt);
    }

    [Fact]
    public async Task Editar_sin_password_no_revoca_nada()
    {
        var target = _factory.Users.AddUser("qa.renombrado@bit.test", "Password123!", "consultor", mustChange: false);
        var admin = ClientFor("qa.admin.edita@bit.test", "admin");

        var res = await admin.PutAsJsonAsync($"/auth/users/{target.UserId}",
            new { full_name = "Nuevo Nombre" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Null(_factory.Users.Find("qa.renombrado@bit.test")!.TokensRevokedAt);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeAppUserStore Users { get; } = new();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

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
