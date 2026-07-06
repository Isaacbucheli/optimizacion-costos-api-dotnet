using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Tests.CostEngine.Api; // FakeAnalysisAccess

namespace OptimizacionCostos.Api.Tests;

/// <summary>
/// Regresión de autorización de los controllers de AzureIntegration (credenciales + suscripciones).
/// Las 7 mutaciones son admin-only ([Authorize(Roles = Roles.Admin)]), igual que la gestión de
/// clientes/usuarios y que el front React (sección "Clientes" adminOnly + botones isAdmin).
/// El 403 lo produce el middleware de autorización antes de construir el controller, así que estas
/// pruebas no necesitan BD ni fakes. Verifican que lector Y consultor quedan bloqueados (no solo lector).
/// </summary>
public class AzureIntegrationAccessControlTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public AzureIntegrationAccessControlTests(TestAppFactory factory) => _factory = factory;

    /// <summary>Las 7 mutaciones protegidas: (método, ruta, cuerpo JSON o null).</summary>
    public static IEnumerable<object?[]> MutatingEndpoints() =>
    [
        ["POST", "/azure/credentials", "{\"client_id\":1,\"credential_name\":\"x\",\"tenant_id\":\"t\",\"app_client_id\":\"a\",\"client_secret\":\"s\"}"],
        ["PUT", "/azure/credentials/5/secret", "{\"client_secret\":\"s\"}"],
        ["PUT", "/azure/credentials/5", "{\"is_active\":false}"],
        ["POST", "/azure/credentials/5/test", null],
        ["POST", "/azure/subscriptions", "{\"client_id\":1,\"credential_id\":1,\"subscription_id\":\"s\"}"],
        ["PUT", "/azure/subscriptions/5", "{\"is_managed\":false}"],
        ["POST", "/azure/subscriptions/sync-client/9", null],
    ];

    private HttpClient ClientFor(string email, string role)
    {
        _factory.Directory.Add(email, role);
        var client = _factory.CreateClient();
        var token = BitJwt.Create(TestAppFactory.Secret, email, "Test User", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static HttpRequestMessage Build(string method, string path, string? jsonBody)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return req;
    }

    [Theory]
    [MemberData(nameof(MutatingEndpoints))]
    public async Task Lector_no_puede_mutar(string method, string path, string? body)
    {
        var res = await ClientFor("lector@bit.ec", Roles.Lector).SendAsync(Build(method, path, body));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Theory]
    [MemberData(nameof(MutatingEndpoints))]
    public async Task Consultor_no_puede_mutar(string method, string path, string? body)
    {
        // Gating a admin (no Editors): un consultor también queda bloqueado, igual que en el front React.
        var res = await ClientFor("consultor@bit.ec", Roles.Consultor).SendAsync(Build(method, path, body));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Admin_pasa_el_gate_de_rol()
    {
        // Admin supera [Authorize(Roles=Admin)] y el check de acceso (global); con cuerpo vacío
        // la validación de campos responde 400 (NO 403), probando que el gate no lo bloquea.
        // Se faketea IAnalysisAccess para que el check de acceso no abra conexión SQL real.
        _factory.Directory.Add("admin@bit.ec", Roles.Admin);
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IAnalysisAccess>();
            s.AddScoped<IAnalysisAccess>(_ => new FakeAnalysisAccess()); // admin => acceso global
        }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BitJwt.Create(TestAppFactory.Secret, "admin@bit.ec", "Test User", Roles.Admin));

        var res = await client.SendAsync(Build("POST", "/azure/credentials", "{}"));
        Assert.NotEqual(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
