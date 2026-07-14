using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.AzureIntegration.UserSessions;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests.AzureIntegration.UserSessions;

public class UserSessionsApiTestFactory : WebApplicationFactory<Program>
{
    public const string Secret = "test-secret-con-mas-de-32-caracteres-1234567890";

    public FakeUserDirectory Directory { get; } = new();
    public FakeDeviceCodeFlow Flow { get; } = new();

    public UserSessionsApiTestFactory(bool enabled = true)
    {
        Environment.SetEnvironmentVariable("JWT_SECRET", Secret);
        Environment.SetEnvironmentVariable("USER_SESSION_AUTH_ENABLED", enabled ? "true" : "false");
        Environment.SetEnvironmentVariable("USER_SESSION_ALLOWED_EMAILS", "consultor@bit.ec");
        Directory.Add("admin@bit.ec", Roles.Admin);
        Directory.Add("consultor@bit.ec", Roles.Consultor);
        Directory.Add("otro@bit.ec", Roles.Consultor);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserDirectory>();
            services.AddSingleton<IUserDirectory>(Directory);
            services.RemoveAll<IDeviceCodeFlow>();
            services.AddSingleton<IDeviceCodeFlow>(Flow);
            services.RemoveAll<IModulePermissionStore>();
            services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
        });
    }
}

// Serializar: las factories manipulan variables de entorno de proceso.
[Collection("UserSessionsEnv")]
public class UserSessionsApiTests
{
    private static HttpClient Client(UserSessionsApiTestFactory f, string email, string role)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BitJwt.Create(UserSessionsApiTestFactory.Secret, email, "Test User", role));
        return c;
    }

    [Fact]
    public async Task Flag_apagado_devuelve_404()
    {
        using var f = new UserSessionsApiTestFactory(enabled: false);
        var res = await Client(f, "admin@bit.ec", Roles.Admin).GetAsync("/azure/user-sessions/current");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Usuario_no_permitido_devuelve_403()
    {
        using var f = new UserSessionsApiTestFactory();
        var res = await Client(f, "otro@bit.ec", Roles.Consultor).GetAsync("/azure/user-sessions/current");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Theory]
    [InlineData("admin@bit.ec", Roles.Admin)]          // admin siempre pasa
    [InlineData("consultor@bit.ec", Roles.Consultor)]  // en la lista
    public async Task Start_devuelve_202_y_status_expone_el_codigo(string email, string role)
    {
        using var f = new UserSessionsApiTestFactory();
        var client = Client(f, email, role);

        var start = await client.PostAsync("/azure/user-sessions", content: null);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

        // El fake emite el prompt sincrónicamente en Start; el status ya debe traer el código.
        var status = JsonNode.Parse(await client.GetStringAsync("/azure/user-sessions/current"))!;
        Assert.Equal("pending_device", status["status"]!.GetValue<string>());
        Assert.Equal("TESTCODE", status["user_code"]!.GetValue<string>());
        Assert.Equal("https://login.microsoft.com/device", status["verification_url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sin_sesion_el_status_es_none_y_delete_es_idempotente()
    {
        using var f = new UserSessionsApiTestFactory();
        var client = Client(f, "admin@bit.ec", Roles.Admin);

        var status = JsonNode.Parse(await client.GetStringAsync("/azure/user-sessions/current"))!;
        Assert.Equal("none", status["status"]!.GetValue<string>());

        var del = await client.DeleteAsync("/azure/user-sessions/current");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
    }
}
