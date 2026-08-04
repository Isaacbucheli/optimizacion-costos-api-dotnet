using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
/// Pruebas del CRUD del catálogo de rutas de migración (Fase 2 Entrega 4, Task 3): store fake en
/// memoria (sin SQL real — eso lo cubre BoletinMigracionStoreTests), pero pasan por el pipeline MVC
/// completo (auth, roles, RequireModule, model binding). Espejo de BoletinLifecycleApiTests: mismo
/// harness (Factory con fake directory/roles, ClientFor, Json), mismas reglas de validación
/// (BuildMigracionFields es el espejo de BuildLifecycleFields con las columnas de migración).
/// </summary>
public sealed class BoletinMigracionApiTests : IClassFixture<BoletinMigracionApiTests.Factory>
{
    private readonly Factory _factory;
    public BoletinMigracionApiTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string email, string role)
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        var token = BitJwt.Create(Factory.Secret, email, "Test User", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ---- Permisos: GET libre, mutaciones solo consultor/admin ----

    [Fact]
    public async Task Get_como_lector_devuelve_200()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.GetAsync("/boletin/migracion");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Post_como_lector_devuelve_403()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.PostAsync("/boletin/migracion", Json("""
            {"clave":"x","desde":"D","hacia":"H","notas":"N","match_pattern":"p"}
            """));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Put_como_lector_devuelve_403()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.PutAsync("/boletin/migracion/1", Json("{\"notas\":\"x\"}"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Delete_como_lector_devuelve_403()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.DeleteAsync("/boletin/migracion/1");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- POST ----

    [Fact]
    public async Task Post_valido_devuelve_200_con_id()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsync("/boletin/migracion", Json("""
            {"clave":"nueva-ruta","desde":"VMs clásicas","hacia":"VMs ARM","notas":"Migrar antes de EOL",
             "match_pattern":"nueva ruta"}
            """));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("id").GetInt32() > 0);
    }

    [Fact]
    public async Task Post_clave_duplicada_devuelve_409()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsync("/boletin/migracion", Json("""
            {"clave":"vm-classic","desde":"D","hacia":"H","notas":"N","match_pattern":"p"}
            """));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ya existe una ruta activa con la clave 'vm-classic'.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Post_sin_campo_obligatorio_devuelve_400_con_detail_que_nombra_el_campo()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsync("/boletin/migracion", Json("""
            {"clave":"otra-ruta","desde":"","hacia":"H","notas":"N","match_pattern":"p"}
            """));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Falta el campo obligatorio 'desde'", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Post_con_match_pattern_en_mayusculas_llega_al_store_en_minusculas_y_trim()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsync("/boletin/migracion", Json("""
            {"clave":"ruta-mayus","desde":"D","hacia":"H","notas":"N","match_pattern":"  MI Patron ESPECIAL  "}
            """));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("mi patron especial", _factory.Store.LastFields!["match_pattern"]);
    }

    // ---- PUT ----

    [Fact]
    public async Task Put_body_vacio_devuelve_400_nada_que_actualizar()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/migracion/1", Json("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nada que actualizar", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_con_desde_numerico_devuelve_400()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/migracion/1", Json("{\"desde\":5}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("El campo 'desde' debe ser texto", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_con_learn_more_url_no_http_devuelve_400()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/migracion/1", Json("{\"learn_more_url\":\"/relativo\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("learn_more_url debe ser una URL http(s) absoluta", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_con_match_pattern_en_mayusculas_llega_al_store_en_minusculas_y_trim()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/migracion/1", Json("{\"match_pattern\":\"  OTRO Patron  \"}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("otro patron", _factory.Store.LastFields!["match_pattern"]);
    }

    [Fact]
    public async Task Put_valido_devuelve_200()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/migracion/1", Json("{\"notas\":\"Notas actualizadas\"}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ---- DELETE ----

    [Fact]
    public async Task Delete_id_inexistente_devuelve_404()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.DeleteAsync("/boletin/migracion/9999");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ---- Fixture: API real en memoria, solo se fake-an auth y el store de migración ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeBoletinMigracionStore Store { get; } = new FakeBoletinMigracionStore().Seed();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IBoletinMigracionStore>();
                services.AddSingleton<IBoletinMigracionStore>(Store);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
            });
        }
    }

    /// <summary>Catálogo de rutas de migración en memoria (sin BD) para probar el pipeline HTTP
    /// completo. LastFields expone lo que el controller le mandó a Create/UpdateAsync tras la
    /// normalización (match_pattern trim+minúsculas) — así los tests verifican que la normalización
    /// pasó por el CONTROLLER, no que el fake la haga por su cuenta.</summary>
    public sealed class FakeBoletinMigracionStore : IBoletinMigracionStore
    {
        private readonly List<MigracionEntry> _entries = [];
        private int _seq;

        public IReadOnlyDictionary<string, object?>? LastFields { get; private set; }

        public FakeBoletinMigracionStore Seed()
        {
            _entries.Add(new MigracionEntry(++_seq, "vm-classic", "VMs clásicas", "VMs ARM",
                "Migrar antes del EOL", "classic vm", "https://aka.ms/vm-migrate", true));
            return this;
        }

        public Task<IReadOnlyList<MigracionEntry>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MigracionEntry>>(_entries.Where(e => includeInactive || e.IsActive).ToList());

        public Task<int> CreateAsync(IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
        {
            LastFields = fields;
            var clave = fields.TryGetValue("clave", out var c) ? c as string : null;
            if (clave is not null && _entries.Any(e => e.Clave == clave && e.IsActive))
                throw new MigracionClaveDuplicadaException(clave);
            var entry = Apply(new MigracionEntry(++_seq, "", "", "", "", "", null, true), fields);
            _entries.Add(entry);
            return Task.FromResult(entry.Id);
        }

        public Task<bool> UpdateAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
        {
            LastFields = fields;
            var idx = _entries.FindIndex(e => e.Id == id);
            if (idx < 0) return Task.FromResult(false);
            _entries[idx] = Apply(_entries[idx], fields);
            return Task.FromResult(true);
        }

        public Task<bool> SoftDeleteAsync(int id, CancellationToken ct = default)
        {
            var idx = _entries.FindIndex(e => e.Id == id);
            if (idx < 0) return Task.FromResult(false);
            _entries[idx] = _entries[idx] with { IsActive = false };
            return Task.FromResult(true);
        }

        private static MigracionEntry Apply(MigracionEntry e, IReadOnlyDictionary<string, object?> f) => e with
        {
            Clave = f.TryGetValue("clave", out var v1) && v1 is string s1 ? s1 : e.Clave,
            Desde = f.TryGetValue("desde", out var v2) && v2 is string s2 ? s2 : e.Desde,
            Hacia = f.TryGetValue("hacia", out var v3) && v3 is string s3 ? s3 : e.Hacia,
            Notas = f.TryGetValue("notas", out var v4) && v4 is string s4 ? s4 : e.Notas,
            MatchPattern = f.TryGetValue("match_pattern", out var v5) && v5 is string s5 ? s5 : e.MatchPattern,
            LearnMoreUrl = f.TryGetValue("learn_more_url", out var v6) ? v6 as string : e.LearnMoreUrl,
            IsActive = f.TryGetValue("is_active", out var v7) && v7 is bool b7 ? b7 : e.IsActive,
        };
    }
}
