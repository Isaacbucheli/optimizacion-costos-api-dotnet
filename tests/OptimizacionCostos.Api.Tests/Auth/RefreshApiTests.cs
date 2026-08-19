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
/// Refresh tokens rotatorios (spec DAST 2026-08-19): el login abre una familia; el canje
/// rota (single-use); el reuso dentro de la gracia es una carrera legítima de pestañas y
/// emite un hermano; el reuso fuera de la gracia revoca la familia completa; y el logout
/// mata las familias del usuario.
/// </summary>
public sealed class RefreshApiTests : IClassFixture<RefreshApiTests.Factory>
{
    private readonly Factory _factory;
    public RefreshApiTests(Factory factory) => _factory = factory;

    private sealed record Sesion(string AccessToken, string RefreshToken);

    private async Task<Sesion> Login(string email)
    {
        _factory.Users.AddUser(email, "Password123!", "consultor", mustChange: false);
        _factory.Directory.Add(email, "consultor");
        var res = await _factory.CreateClient().PostAsJsonAsync("/auth/login",
            new { username = email, password = "Password123!" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return new Sesion(
            body.GetProperty("access_token").GetString()!,
            body.GetProperty("refresh_token").GetString()!);
    }

    private async Task<HttpResponseMessage> Canjear(string refreshToken) =>
        await _factory.CreateClient().PostAsJsonAsync("/auth/refresh", new { refresh_token = refreshToken });

    private static async Task<JsonElement> Body(HttpResponseMessage res) =>
        await res.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Login_devuelve_refresh_token()
    {
        _factory.Users.AddUser("qa.login.rt@bit.test", "Password123!", "consultor", mustChange: false);
        var res = await _factory.CreateClient().PostAsJsonAsync("/auth/login",
            new { username = "qa.login.rt@bit.test", password = "Password123!" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await Body(res);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refresh_token").GetString()));
        Assert.InRange(body.GetProperty("refresh_expires_in").GetInt32(), 8 * 3600 - 60, 8 * 3600);
    }

    [Fact]
    public async Task Canje_valido_rota_y_devuelve_un_par_nuevo()
    {
        var login = await Login("qa.rotacion@bit.test");

        var res = await Canjear(login.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await Body(res);
        Assert.NotEqual(login.RefreshToken, body.GetProperty("refresh_token").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("access_token").GetString()));
    }

    [Fact]
    public async Task Un_refresh_desconocido_devuelve_401()
    {
        var res = await Canjear(RefreshTokenCodec.NewToken());
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Reuso_fuera_de_la_gracia_revoca_la_familia_completa()
    {
        var login = await Login("qa.robo@bit.test");
        var primero = await Body(await Canjear(login.RefreshToken));            // rota
        _factory.RefreshStore.EnvejecerUso(login.RefreshToken, TimeSpan.FromSeconds(120)); // > gracia 60s

        var reuso = await Canjear(login.RefreshToken);
        var hijo = await Canjear(primero.GetProperty("refresh_token").GetString()!);

        Assert.Equal(HttpStatusCode.Unauthorized, reuso.StatusCode); // el reuso se rechaza
        Assert.Equal(HttpStatusCode.Unauthorized, hijo.StatusCode);  // y arrastra a toda la familia
    }

    [Fact]
    public async Task Reuso_dentro_de_la_gracia_emite_un_hermano_sin_castigo()
    {
        var login = await Login("qa.pestanas@bit.test");
        var primero = await Body(await Canjear(login.RefreshToken));  // pestaña A

        var segundo = await Canjear(login.RefreshToken);              // pestaña B, <60s después
        var hijoA = await Canjear(primero.GetProperty("refresh_token").GetString()!);

        Assert.Equal(HttpStatusCode.OK, segundo.StatusCode);
        Assert.Equal(HttpStatusCode.OK, hijoA.StatusCode);            // la familia sigue viva
    }

    [Fact]
    public async Task Logout_revoca_las_familias_y_el_refresh_muere()
    {
        var login = await Login("qa.logout.rt@bit.test");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var logout = await client.PostAsync("/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        var res = await Canjear(login.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeAppUserStore Users { get; } = new();
        public FakeRefreshTokenStore RefreshStore { get; } = new();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IAppUserStore>();
                services.AddSingleton<IAppUserStore>(Users);
                services.RemoveAll<IRefreshTokenStore>();
                services.AddSingleton<IRefreshTokenStore>(RefreshStore);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
            });
        }
    }
}
