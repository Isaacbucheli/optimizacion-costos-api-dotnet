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
using OptimizacionCostos.Api.Features.AlertCatalog;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests.Security;

/// <summary>
/// Límite de tasa global. La API no tenía ninguno: el escaneo del 2026-08-03 disparó seis revisiones
/// de accesos reales contra el tenant de un cliente porque las guardas de concurrencia solo evitan
/// corridas simultáneas, y cada una terminaba antes de que llegara el siguiente request.
///
/// El umbral se baja a 5 por minuto vía RATE_LIMIT_PER_MINUTE para que las pruebas no tengan que
/// mandar 101 peticiones. Que el umbral sea configurable no es un artificio de test: es la válvula de
/// escape para subirlo en producción sin esperar un deploy.
/// </summary>
public sealed class RateLimitTests : IClassFixture<RateLimitTests.Factory>
{
    private const int Limite = 5;
    private readonly Factory _factory;
    public RateLimitTests(Factory factory) => _factory = factory;

    private HttpClient ClienteDe(string email)
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, Roles.Admin);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BitJwt.Create(Factory.Secret, email, "Test User", Roles.Admin));
        return client;
    }

    [Fact]
    public async Task Pasado_el_limite_responde_429_con_detail_y_retry_after()
    {
        var client = ClienteDe("rafaga@bit.ec");

        for (var i = 1; i <= Limite; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alert-catalog")).StatusCode);

        var rechazada = await client.GetAsync("/alert-catalog");

        Assert.Equal(HttpStatusCode.TooManyRequests, rechazada.StatusCode);
        Assert.NotNull(rechazada.Headers.RetryAfter);
        var body = await rechazada.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Demasiadas peticiones", body.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task Cada_usuario_tiene_su_propia_cuota()
    {
        // Sin particionar por usuario, el primero en gastar la cuota dejaría a los otros 23 afuera.
        var uno = ClienteDe("cuota-uno@bit.ec");
        for (var i = 1; i <= Limite + 1; i++) await uno.GetAsync("/alert-catalog");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await uno.GetAsync("/alert-catalog")).StatusCode);

        var otro = ClienteDe("cuota-dos@bit.ec");
        Assert.Equal(HttpStatusCode.OK, (await otro.GetAsync("/alert-catalog")).StatusCode);
    }

    [Fact]
    public async Task El_429_conserva_las_cabeceras_de_seguridad_y_de_CORS()
    {
        // Mismo motivo que en el 500: si el rechazo pierde la cabecera de CORS, el navegador reporta
        // un fallo de red y el front nunca llega a mostrar el mensaje del límite.
        var client = ClienteDe("cabeceras@bit.ec");
        for (var i = 1; i <= Limite + 1; i++) await client.GetAsync("/alert-catalog");

        var req = new HttpRequestMessage(HttpMethod.Get, "/alert-catalog");
        req.Headers.Add("Origin", Factory.OrigenPermitido);
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("default-src 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(Factory.OrigenPermitido, res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Health_queda_exento()
    {
        // El probe de arranque de App Service pega a /health; un 429 lo marcaria como no sano y el
        // sitio no llegaria a levantar.
        var client = _factory.CreateClient();
        for (var i = 1; i <= Limite * 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task El_trafico_anonimo_no_gasta_la_cuota_de_un_usuario()
    {
        // El anónimo se particiona por IP. Si compartiera cubeta con los autenticados, cualquiera
        // podría dejar sin servicio a un usuario con solo pegarle al login.
        var anonimo = _factory.CreateClient();
        for (var i = 1; i <= Limite + 2; i++) await anonimo.GetAsync("/alert-catalog"); // 401, pero cuentan

        var autenticado = ClienteDe("no-me-afecta@bit.ec");
        Assert.Equal(HttpStatusCode.OK, (await autenticado.GetAsync("/alert-catalog")).StatusCode);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = "test-secret-con-mas-de-32-caracteres-1234567890";
        public const string OrigenPermitido = "https://front-de-prueba-ratelimit.example";

        public FakeUserDirectory Directory { get; } = new();
        public FakeAlertCatalogStore Store { get; } = new FakeAlertCatalogStore().Seed();

        public Factory()
        {
            // JWT_SECRET sí va por variable de entorno, siguiendo la convención de los demás
            // factories, y es inocuo porque todos usan el mismo valor.
            Environment.SetEnvironmentVariable("JWT_SECRET", Secret);
            // CORS_ORIGINS y RATE_LIMIT_PER_MINUTE NO: son globales al proceso y xUnit corre las
            // clases en paralelo, así que un valor distinto en otro factory rompería a este (o este a
            // aquél). Se limpian y se pasan por UseSetting, que es por host. AppConfig.Get prioriza la
            // variable de entorno y cae a la configuración, así que hay que dejarla vacía.
            Environment.SetEnvironmentVariable("CORS_ORIGINS", null);
            Environment.SetEnvironmentVariable("RATE_LIMIT_PER_MINUTE", null);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("CORS_ORIGINS", OrigenPermitido);
            builder.UseSetting("RATE_LIMIT_PER_MINUTE", Limite.ToString());
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
}
