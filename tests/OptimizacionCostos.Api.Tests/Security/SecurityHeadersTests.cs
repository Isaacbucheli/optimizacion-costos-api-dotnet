using System.Net;
using System.Net.Http.Headers;
using OptimizacionCostos.Api.Auth;

namespace OptimizacionCostos.Api.Tests.Security;

/// <summary>
/// Cabeceras de seguridad de la API (hallazgo A3 de la revisión de seguridad). Deben salir
/// en TODA respuesta, incluidas las de error: un escáner que pega sin token recibe un 401 y
/// ahí también las evalúa.
///
/// Ojo: el banner "Server: Kestrel" (A2) NO se puede comprobar acá porque TestServer no usa
/// Kestrel; una aserción de ausencia pasaría sola y simularía cobertura. Se verifica contra
/// la API desplegada.
///
/// El caso de Swagger vive aparte, en SwaggerGatingTests, para que ninguna otra prueba dependa
/// de si el flag está prendido.
/// </summary>
public sealed class SecurityHeadersTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public SecurityHeadersTests(TestAppFactory factory) => _factory = factory;

    private static void AssertCabeceras(HttpResponseMessage res)
    {
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(res.Headers.CacheControl?.NoStore, "Cache-Control debe ser no-store.");

        var hsts = res.Headers.GetValues("Strict-Transport-Security").Single();
        Assert.Contains("max-age=31536000", hsts);
        Assert.Contains("includeSubDomains", hsts);

        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
    }

    [Fact]
    public async Task Una_respuesta_correcta_trae_las_cabeceras()
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add("admin@bit.ec", Roles.Admin);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BitJwt.Create(TestAppFactory.Secret, "admin@bit.ec", "Test User", Roles.Admin));

        var res = await client.GetAsync("/alert-catalog");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        AssertCabeceras(res);
    }

    [Fact]
    public async Task Una_respuesta_de_error_tambien_las_trae()
    {
        // Sin token: es justo el caso que ve un escáner anónimo.
        var res = await _factory.CreateClient().GetAsync("/alert-catalog");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        AssertCabeceras(res);
    }

}
