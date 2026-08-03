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
/// Pruebas del CRUD del catálogo de lifecycle (fin de soporte) del Boletín: NO requieren SQL real
/// (store fake en memoria), pero SÍ pasan por el pipeline MVC completo (auth, roles, RequireModule,
/// model binding) — cubren el undelete transparente vs conflicto real (fix A1), la validación de
/// learn_more_url (A2), el bypass de tipos (A3) y el PUT vacío (A4). BoletinLifecycleStoreTests no
/// puede tocar estos casos porque esa clase habla con Azure SQL real. Espejo del patrón de
/// PolicyCatalogApiTests/AlertCatalogApiTests.
/// </summary>
public sealed class BoletinLifecycleApiTests : IClassFixture<BoletinLifecycleApiTests.Factory>
{
    private readonly Factory _factory;
    public BoletinLifecycleApiTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string email, string role)
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        var token = BitJwt.Create(Factory.Secret, email, "Test User", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ---- A1: undelete transparente vs conflicto real ----

    [Fact]
    public async Task Crear_con_clave_desactivada_la_reactiva_en_vez_de_500()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsync("/boletin/lifecycle", Json("""
            {"clave":"sql-2012","producto":"SQL Server 2012 (reactivado)","categoria":"bd",
             "match_field":"sql_image_offer","match_pattern":"sql2012","end_of_support":"2022-07-12",
             "recomendacion":"Actualizar"}
            """));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetInt32();
        Assert.Equal(2, id); // mismo id de la fila desactivada del seed (undelete, no una fila nueva)

        var entry = await _factory.Store.GetAsync(id);
        Assert.NotNull(entry);
        Assert.True(entry!.IsActive);
        Assert.Equal("SQL Server 2012 (reactivado)", entry.Producto);
    }

    [Fact]
    public async Task Crear_con_clave_ya_activa_devuelve_409()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsync("/boletin/lifecycle", Json("""
            {"clave":"windows-server-2012","producto":"Duplicado","categoria":"so",
             "match_field":"os_name","match_pattern":"windows server 2012","end_of_support":"2023-10-10",
             "recomendacion":"x"}
            """));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ya existe una entrada activa con la clave 'windows-server-2012'.", body.GetProperty("detail").GetString());
    }

    // ---- A2: learn_more_url debe ser http(s) absoluta ----

    [Fact]
    public async Task Crear_con_learn_more_url_relativa_devuelve_400()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsync("/boletin/lifecycle", Json("""
            {"clave":"nueva-clave-1","producto":"P","categoria":"so","match_field":"os_name",
             "match_pattern":"p","end_of_support":"2030-01-01","recomendacion":"r","learn_more_url":"/relativo"}
            """));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("learn_more_url debe ser una URL http(s) absoluta", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Crear_con_learn_more_url_https_valida_pasa()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsync("/boletin/lifecycle", Json("""
            {"clave":"nueva-clave-2","producto":"P","categoria":"so","match_field":"os_name",
             "match_pattern":"p2","end_of_support":"2030-01-01","recomendacion":"r","learn_more_url":"https://aka.ms/x"}
            """));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ---- A3: number-bypass en campos de texto ----

    [Fact]
    public async Task Crear_con_end_of_support_numerico_devuelve_400()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsync("/boletin/lifecycle", Json("""
            {"clave":"nueva-clave-3","producto":"P","categoria":"so","match_field":"os_name",
             "match_pattern":"p3","end_of_support":1893456000,"recomendacion":"r"}
            """));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("El campo 'end_of_support' debe ser texto", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_is_active_como_bool_sigue_funcionando()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/lifecycle/1", Json("{\"is_active\":false}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ---- A4: PUT sin campos ----

    [Fact]
    public async Task Put_sin_campos_devuelve_400()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/lifecycle/1", Json("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Nada que actualizar", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_solo_con_clave_desconocida_devuelve_400()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/lifecycle/1", Json("{\"clave_desconocida\":1}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- Fixture: API real en memoria, solo se fake-an auth y el store de lifecycle ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeBoletinLifecycleStore Store { get; } = new FakeBoletinLifecycleStore().Seed();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IBoletinLifecycleStore>();
                services.AddSingleton<IBoletinLifecycleStore>(Store);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
            });
        }
    }

    /// <summary>Catálogo de lifecycle en memoria (sin BD) para probar el pipeline HTTP completo,
    /// incluida la lógica de undelete-vs-conflicto de CreateAsync (espejo simplificado de
    /// BoletinLifecycleStore.CreateAsync; la versión SQL real la cubren
    /// BoletinLifecycleStoreTests.DecideCreateOutcome + el E2E manual contra la BD).</summary>
    public sealed class FakeBoletinLifecycleStore : IBoletinLifecycleStore
    {
        private readonly List<LifecycleEntry> _entries = [];
        private int _seq;

        public FakeBoletinLifecycleStore Seed()
        {
            _entries.Add(new LifecycleEntry(++_seq, "windows-server-2012", "Windows Server 2012", "so",
                "os_name", "windows server 2012", new DateOnly(2023, 10, 10), "Actualizar a una versión soportada", null, true));
            _entries.Add(new LifecycleEntry(++_seq, "sql-2012", "SQL Server 2012", "bd",
                "sql_image_offer", "sql2012", new DateOnly(2022, 7, 12), "Actualizar", null, false)); // desactivada
            return this;
        }

        public Task<IReadOnlyList<LifecycleEntry>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LifecycleEntry>>(_entries.Where(e => includeInactive || e.IsActive).ToList());

        public Task<LifecycleEntry?> GetAsync(int id, CancellationToken ct = default)
            => Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));

        public Task<int> CreateAsync(IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
        {
            var clave = fields.TryGetValue("clave", out var c) ? c as string : null;
            var existing = clave is null ? null : _entries.FirstOrDefault(e => e.Clave == clave);
            if (existing is not null)
            {
                if (existing.IsActive) throw new LifecycleClaveDuplicadaException(clave!);
                var reactivated = Apply(existing, fields) with { IsActive = true };
                _entries[_entries.IndexOf(existing)] = reactivated;
                return Task.FromResult(existing.Id);
            }

            var entry = Apply(new LifecycleEntry(++_seq, "", "", "so", "os_name", "", DateOnly.MinValue, "", null, true), fields);
            _entries.Add(entry);
            return Task.FromResult(entry.Id);
        }

        public Task<bool> UpdateAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
        {
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

        private static LifecycleEntry Apply(LifecycleEntry e, IReadOnlyDictionary<string, object?> f) => e with
        {
            Clave = f.TryGetValue("clave", out var v1) && v1 is string s1 ? s1 : e.Clave,
            Producto = f.TryGetValue("producto", out var v2) && v2 is string s2 ? s2 : e.Producto,
            Categoria = f.TryGetValue("categoria", out var v3) && v3 is string s3 ? s3 : e.Categoria,
            MatchField = f.TryGetValue("match_field", out var v4) && v4 is string s4 ? s4 : e.MatchField,
            MatchPattern = f.TryGetValue("match_pattern", out var v5) && v5 is string s5 ? s5 : e.MatchPattern,
            EndOfSupport = f.TryGetValue("end_of_support", out var v6) && v6 is string s6 && DateOnly.TryParse(s6, out var d6) ? d6 : e.EndOfSupport,
            Recomendacion = f.TryGetValue("recomendacion", out var v7) && v7 is string s7 ? s7 : e.Recomendacion,
            LearnMoreUrl = f.TryGetValue("learn_more_url", out var v8) ? v8 as string : e.LearnMoreUrl,
            IsActive = f.TryGetValue("is_active", out var v9) && v9 is bool b9 ? b9 : e.IsActive,
        };
    }
}
