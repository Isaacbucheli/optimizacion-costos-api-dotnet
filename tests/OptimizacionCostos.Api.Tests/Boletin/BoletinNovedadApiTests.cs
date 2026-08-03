using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.Boletin;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests.Boletin;

/// <summary>
/// Pruebas de la ingesta GLOBAL de novedades (POST ingestar / GET / PUT): store fake en memoria (sin
/// SQL/HTTP reales — eso lo cubren el E2E manual), pero pasan por el pipeline MVC completo (auth,
/// roles, RequireModule, model binding). Cubren: conteos del POST, listado del GET, whitelist estricta
/// del PUT (solo categoria_bit/is_active, con validación de valores) y el 502 controlado cuando el
/// feed viene roto o inalcanzable (nunca un 500 crudo). Espejo de BoletinLifecycleApiTests.
/// </summary>
public sealed class BoletinNovedadApiTests : IClassFixture<BoletinNovedadApiTests.Factory>
{
    private readonly Factory _factory;
    public BoletinNovedadApiTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string email, string role)
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        var token = BitJwt.Create(Factory.Secret, email, "Test User", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ---- POST /boletin/novedades/ingestar ----

    [Fact]
    public async Task Ingestar_devuelve_conteos_y_total_activas()
    {
        _factory.Store.Nuevas = 3;
        _factory.Store.Traducidas = 2;
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsync("/boletin/novedades/ingestar", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("nuevas").GetInt32());
        Assert.Equal(2, body.GetProperty("traducidas").GetInt32());
        Assert.Equal(_factory.Store.ActivasCount, body.GetProperty("total_activas").GetInt32());
    }

    [Fact]
    public async Task Ingestar_con_feed_roto_devuelve_502_no_500()
    {
        _factory.Store.ThrowOnIngest = new XmlException("XML truncado");
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        try
        {
            var res = await client.PostAsync("/boletin/novedades/ingestar", null);
            Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("No se pudo leer el feed de Azure Updates. Intenta de nuevo.", body.GetProperty("detail").GetString());
        }
        finally { _factory.Store.ThrowOnIngest = null; }
    }

    [Fact]
    public async Task Ingestar_con_feed_inalcanzable_devuelve_502_no_500()
    {
        _factory.Store.ThrowOnIngest = new HttpRequestException("conexión rechazada");
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        try
        {
            var res = await client.PostAsync("/boletin/novedades/ingestar", null);
            Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        }
        finally { _factory.Store.ThrowOnIngest = null; }
    }

    [Fact]
    public async Task Ingestar_como_lector_devuelve_403()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.PostAsync("/boletin/novedades/ingestar", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Módulo no permitido para su perfil", body.GetProperty("detail").GetString());
    }

    // ---- GET /boletin/novedades ----

    [Fact]
    public async Task Listar_devuelve_solo_activas_por_defecto()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.GetAsync("/boletin/novedades");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.All(body.EnumerateArray(), e => Assert.True(e.GetProperty("is_active").GetBoolean()));
    }

    [Fact]
    public async Task Listar_con_include_inactive_devuelve_todas()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.GetAsync("/boletin/novedades?include_inactive=true");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.EnumerateArray(), e => !e.GetProperty("is_active").GetBoolean());
    }

    // ---- PUT /boletin/novedades/{id} — whitelist estricta ----

    [Fact]
    public async Task Put_categoria_bit_valida_devuelve_200()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades/1", Json("{\"categoria_bit\":\"seguridad_identidad\"}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Put_categoria_bit_invalida_devuelve_400()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades/1", Json("{\"categoria_bit\":\"no_existe\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("categoria_bit", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_categoria_bit_numerica_devuelve_400()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades/1", Json("{\"categoria_bit\":123}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_is_active_bool_devuelve_200()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades/1", Json("{\"is_active\":false}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Put_is_active_no_bool_devuelve_400()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades/1", Json("{\"is_active\":\"si\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_intenta_pisar_titulo_es_lo_ignora_y_devuelve_400_por_vacio()
    {
        // titulo_es/descripcion_es NO están en la whitelist: si es el único campo, no hay nada que
        // actualizar (whitelist estricta, no un bypass silencioso).
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades/1", Json("{\"titulo_es\":\"Hackeado\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nada que actualizar", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_sin_campos_devuelve_400()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades/1", Json("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_id_inexistente_devuelve_404()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades/9999", Json("{\"is_active\":false}"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Put_como_lector_devuelve_403()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.PutAsync("/boletin/novedades/1", Json("{\"is_active\":false}"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- Fixture: API real en memoria, solo se fake-an auth y el store de novedades ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeBoletinNovedadStore Store { get; } = new FakeBoletinNovedadStore().Seed();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IBoletinNovedadStore>();
                services.AddSingleton<IBoletinNovedadStore>(Store);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
            });
        }
    }

    /// <summary>Store de novedades en memoria (sin BD/HTTP) para probar el pipeline HTTP completo.
    /// La ingesta real (dedupe SQL + descarga RSS) la cubren el E2E manual; acá solo se simulan sus
    /// resultados (conteos configurables, excepción configurable para el camino 502).</summary>
    public sealed class FakeBoletinNovedadStore : IBoletinNovedadStore
    {
        private readonly List<NovedadRow> _rows = [];
        private int _seq;

        public int Nuevas { get; set; }
        public int Traducidas { get; set; }
        public Exception? ThrowOnIngest { get; set; }
        public int ActivasCount => _rows.Count(r => r.IsActive);

        public FakeBoletinNovedadStore Seed()
        {
            _rows.Add(new NovedadRow(++_seq, "guid-1", "New feature launched", "Nueva funcionalidad lanzada",
                "Description in English", "Descripción en español", "https://azure.microsoft.com/updates/1",
                "launched", "productividad_ia", "[\"AI + machine learning\"]",
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), true));
            _rows.Add(new NovedadRow(++_seq, "guid-2", "Feature retired", null,
                "Retired description", null, "https://azure.microsoft.com/updates/2",
                "otro", "resiliencia_plataforma", "[]",
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), false)); // desactivada
            return this;
        }

        public Task<(int Nuevas, int Traducidas)> IngestAsync(CancellationToken ct = default)
        {
            if (ThrowOnIngest is not null) throw ThrowOnIngest;
            return Task.FromResult((Nuevas, Traducidas));
        }

        public Task<IReadOnlyList<NovedadRow>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NovedadRow>>(_rows.Where(r => includeInactive || r.IsActive).ToList());

        public Task<bool> UpdateAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
        {
            var idx = _rows.FindIndex(r => r.Id == id);
            if (idx < 0) return Task.FromResult(false);
            var r = _rows[idx];
            _rows[idx] = r with
            {
                CategoriaBit = fields.TryGetValue("categoria_bit", out var cb) && cb is string s ? s : r.CategoriaBit,
                IsActive = fields.TryGetValue("is_active", out var ia) && ia is bool b ? b : r.IsActive,
            };
            return Task.FromResult(true);
        }
    }
}
