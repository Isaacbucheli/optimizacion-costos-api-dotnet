using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.AlertCatalog;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests.Security;

/// <summary>
/// Red de última instancia del pipeline. Antes, una excepción que nadie manejara salía de Kestrel
/// como conexión cortada: el cliente veía un socket cerrado, no una respuesta HTTP. El ZAP del
/// 2026-08-03 leyó justo eso (causado por el error 8152 de SQL Server al truncar texto) como un
/// "Format String Error" de riesgo Medio.
///
/// Se comprueba que ahora sea un 500 con cuerpo JSON, que NO filtre el detalle de la excepción, y
/// que conserve las cabeceras de seguridad — por eso el middleware no usa UseExceptionHandler, que
/// hace Response.Clear() y se las lleva.
/// </summary>
public sealed class UnhandledExceptionTests : IClassFixture<UnhandledExceptionTests.Factory>
{
    private readonly Factory _factory;
    public UnhandledExceptionTests(Factory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add("admin@bit.ec", Roles.Admin);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BitJwt.Create(Factory.Secret, "admin@bit.ec", "Test User", Roles.Admin));
        return client;
    }

    [Fact]
    public async Task Excepcion_sin_manejar_devuelve_500_con_json_y_no_corta_la_conexion()
    {
        var res = await AuthedClient().GetAsync("/alert-catalog");

        // Lo importante: hay respuesta HTTP. Sin el middleware esto era una excepción de socket
        // en el cliente, que es lo que un escáner reporta como fallo de la aplicación.
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Error interno", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task El_500_no_filtra_el_detalle_de_la_excepcion()
    {
        var res = await AuthedClient().GetAsync("/alert-catalog");
        var raw = await res.Content.ReadAsStringAsync();

        // Ni el mensaje sembrado, ni el tipo de excepción, ni rastro de stack trace.
        Assert.DoesNotContain(Factory.MensajeSembrado, raw);
        Assert.DoesNotContain("SqlException", raw);
        Assert.DoesNotContain("StackTrace", raw);
        Assert.DoesNotContain("OptimizacionCostos.Api", raw);
    }

    [Fact]
    public async Task El_500_conserva_las_cabeceras_de_seguridad()
    {
        var res = await AuthedClient().GetAsync("/alert-catalog");

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("default-src 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("max-age=31536000", res.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public async Task El_500_conserva_la_cabecera_de_CORS()
    {
        // Es la razón por la que el middleware NO usa UseExceptionHandler: ese hace Response.Clear()
        // y se lleva esta cabecera. Sin ella el navegador reporta un error de CORS en vez de dejar
        // que el front lea el 500, así que el usuario ve "Failed to fetch" y no el mensaje real.
        // UseCors corre por DENTRO del middleware (se registra después), así que este caso no lo
        // cubre la prueba de cabeceras de seguridad, que verifica el sentido contrario.
        var client = AuthedClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/alert-catalog");
        req.Headers.Add("Origin", Factory.OrigenPermitido);

        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal(Factory.OrigenPermitido,
            res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task El_cache_control_trae_la_directiva_completa()
    {
        // Cierra el plugin 10015 de ZAP ("Re-examine Cache-control Directives"), que lo seguía
        // listando con solo no-store, y es lo que se le informó al equipo de seguridad en julio.
        var res = await AuthedClient().GetAsync("/alert-catalog");

        var cache = res.Headers.CacheControl!;
        Assert.True(cache.NoStore);
        Assert.True(cache.NoCache);
        Assert.True(cache.MustRevalidate);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = "test-secret-con-mas-de-32-caracteres-1234567890";
        public const string MensajeSembrado = "detalle-interno-que-no-debe-salir-al-cliente";

        /// <summary>Origen distintivo para que no choque con nada si otra prueba lee CORS_ORIGINS.</summary>
        public const string OrigenPermitido = "https://front-de-prueba-unhandled.example";

        public FakeUserDirectory Directory { get; } = new();

        public Factory()
        {
            Environment.SetEnvironmentVariable("JWT_SECRET", Secret);
            // CORS_ORIGINS va por UseSetting y no por variable de entorno: la variable es global al
            // proceso y otro factory con un origen distinto rompería la aserción de este. AppConfig
            // prioriza la variable de entorno, así que hay que dejarla vacía para que gane UseSetting.
            Environment.SetEnvironmentVariable("CORS_ORIGINS", null);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("CORS_ORIGINS", OrigenPermitido);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
                // Store que explota igual que lo hacía el UPDATE con texto más largo que la columna.
                services.RemoveAll<IAlertCatalogStore>();
                services.AddSingleton<IAlertCatalogStore>(new ExplotaStore());
            });
        }
    }

    /// <summary>Store que lanza en el primer método que toca el controlador. Simula el 8152.</summary>
    private sealed class ExplotaStore : IAlertCatalogStore
    {
        private static Exception Boom() => new InvalidOperationException(Factory.MensajeSembrado);

        public Task<IReadOnlyList<AlertItem>> ListAlertsAsync(bool includeInactive = false, CancellationToken ct = default)
            => throw Boom();
        public Task<AlertItem?> GetAlertAsync(int alertId, CancellationToken ct = default) => throw Boom();
        public Task<int> CreateAlertAsync(AlertCreate data, CancellationToken ct = default) => throw Boom();
        public Task<bool> UpdateAlertAsync(int alertId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default) => throw Boom();
        public Task<bool> SoftDeleteAlertAsync(int alertId, CancellationToken ct = default) => throw Boom();
        public Task<IReadOnlyList<KqlItem>> ListKqlAsync(bool includeInactive = false, CancellationToken ct = default) => throw Boom();
        public Task<KqlItem?> GetKqlAsync(int kqlId, CancellationToken ct = default) => throw Boom();
        public Task<int> CreateKqlAsync(KqlCreate data, CancellationToken ct = default) => throw Boom();
        public Task<bool> UpdateKqlAsync(int kqlId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default) => throw Boom();
        public Task<bool> SoftDeleteKqlAsync(int kqlId, CancellationToken ct = default) => throw Boom();
    }
}
