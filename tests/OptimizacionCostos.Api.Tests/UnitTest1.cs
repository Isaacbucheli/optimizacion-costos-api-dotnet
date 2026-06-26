using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OptimizacionCostos.Api.Auth;

namespace OptimizacionCostos.Api.Tests;

/// <summary>
/// Pruebas del PoC: NO requieren Azure ni SQL (BD mockeada con fakes), igual que
/// la suite del FastAPI. Cubren interoperabilidad del JWT, checks de rol y contrato JSON.
/// </summary>
public class AlertCatalogApiTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public AlertCatalogApiTests(TestAppFactory factory) => _factory = factory;

    private HttpClient ClientFor(string? email, string? role)
    {
        var client = _factory.CreateClient();
        if (email is not null && role is not null)
        {
            _factory.Directory.Add(email, role);
            var token = BitJwt.Create(TestAppFactory.Secret, email, "Test User", role);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    // ---- Interoperabilidad del JWT (lo critico para coexistir con el FastAPI) ----

    [Fact]
    public async Task Token_estilo_FastAPI_es_aceptado()
    {
        // Token construido byte-a-byte como app/auth.py. Debe validar en .NET.
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var res = await client.GetAsync("/alert-catalog");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Sin_token_devuelve_401()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/alert-catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Token_expirado_devuelve_401()
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add("admin@bit.ec", Roles.Admin);
        var expired = BitJwt.Create(TestAppFactory.Secret, "admin@bit.ec", "Test", Roles.Admin, expiresInSeconds: -120);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expired);
        var res = await client.GetAsync("/alert-catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Firma_invalida_devuelve_401()
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add("admin@bit.ec", Roles.Admin);
        var bad = BitJwt.Create("otro-secreto-distinto-de-32-caracteres-x", "admin@bit.ec", "Test", Roles.Admin);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bad);
        var res = await client.GetAsync("/alert-catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Usuario_no_en_BD_devuelve_401()
    {
        // Token bien firmado pero el email no existe en app_users (fake vacio para ese email).
        var client = _factory.CreateClient();
        var token = BitJwt.Create(TestAppFactory.Secret, "fantasma@bit.ec", "X", Roles.Admin);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/alert-catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- Checks de rol (lector solo lee; admin/consultor mutan) ----

    [Fact]
    public async Task Lector_puede_listar()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.GetAsync("/alert-catalog");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Lector_no_puede_crear()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.PostAsJsonAsync("/alert-catalog", new { name = "Nueva" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Consultor_puede_crear()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsJsonAsync("/alert-catalog", new { name = "Nueva alerta" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Alerta creada", body.GetProperty("message").GetString());
        Assert.True(body.GetProperty("alert_id").GetInt32() > 0);
    }

    [Fact]
    public async Task Crear_sin_nombre_devuelve_400()
    {
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var res = await client.PostAsJsonAsync("/alert-catalog", new { description = "sin nombre" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- Contrato JSON (snake_case, como lo consume el front) ----

    [Fact]
    public async Task Lista_devuelve_snake_case()
    {
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var items = await client.GetFromJsonAsync<JsonElement>("/alert-catalog");
        Assert.True(items.GetArrayLength() >= 1);
        var first = items[0];
        Assert.True(first.TryGetProperty("alert_id", out _));
        Assert.True(first.TryGetProperty("alert_number", out _));
        Assert.True(first.TryGetProperty("is_active", out _));
    }

    [Fact]
    public async Task Eliminar_inexistente_devuelve_404()
    {
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var res = await client.DeleteAsync("/alert-catalog/999999");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Health_es_publico()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
