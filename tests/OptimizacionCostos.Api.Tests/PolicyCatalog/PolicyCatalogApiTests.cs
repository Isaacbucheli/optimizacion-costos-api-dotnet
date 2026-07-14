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
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.PolicyCatalog;

namespace OptimizacionCostos.Api.Tests.PolicyCatalog;

/// <summary>
/// Pruebas del catalogo de politicas (Azure Policy): NO requieren SQL (store fake),
/// pero SI pasan por el pipeline MVC real (auth, roles, model binding, JSON snake_case).
/// Espejo del patron de AlertCatalogApiTests/WafAdminApiTests.
/// </summary>
public sealed class PolicyCatalogApiTests : IClassFixture<PolicyCatalogApiTests.Factory>
{
    private readonly Factory _factory;
    public PolicyCatalogApiTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string? email, string? role)
    {
        var client = _factory.CreateClient();
        if (email is not null && role is not null)
        {
            _factory.Directory.Add(email, role);
            var token = BitJwt.Create(Factory.Secret, email, "Test User", role);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ---- Listado y detalle ----

    [Fact]
    public async Task Lista_devuelve_items_del_fake_en_snake_case()
    {
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var items = await client.GetFromJsonAsync<JsonElement>("/policy-catalog");
        Assert.True(items.GetArrayLength() >= 1);
        var first = items[0];
        Assert.True(first.TryGetProperty("policy_id", out _));
        Assert.True(first.TryGetProperty("policy_number", out _));
        Assert.True(first.TryGetProperty("recommended_effect", out _));
        Assert.True(first.TryGetProperty("azure_cli", out _));
        Assert.True(first.TryGetProperty("is_active", out _));
        Assert.Equal("Política seed uno", first.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Detalle_inexistente_devuelve_404_con_detail()
    {
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var res = await client.GetAsync("/policy-catalog/999999");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Policy not found", body.GetProperty("detail").GetString());
    }

    // ---- Crear (roles + validacion) ----

    [Fact]
    public async Task Crear_sin_nombre_devuelve_400()
    {
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var res = await client.PostAsJsonAsync("/policy-catalog", new { description = "sin nombre" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Consultor_puede_crear()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsJsonAsync("/policy-catalog", new { name = "Nueva política" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Política creada", body.GetProperty("message").GetString());
        Assert.True(body.GetProperty("policy_id").GetInt32() > 0);
    }

    [Fact]
    public async Task Lector_no_puede_crear()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.PostAsJsonAsync("/policy-catalog", new { name = "Nueva" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- Actualizar (exclude_unset + validacion de name) ----

    [Fact]
    public async Task Put_parcial_solo_actualiza_claves_presentes()
    {
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var res = await client.PutAsync("/policy-catalog/1",
            Json("{\"category\":\"FinOps actualizada\",\"clave_desconocida\":\"ignorada\"}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Al store solo llegan las claves presentes en el body Y en la whitelist.
        Assert.NotNull(_factory.Store.LastUpdateFields);
        Assert.Single(_factory.Store.LastUpdateFields!);
        Assert.Equal("FinOps actualizada", _factory.Store.LastUpdateFields!["category"]);

        // El resto de la politica queda intacto (el name no viajo en el body).
        var item = _factory.Store.Find(1)!;
        Assert.Equal("FinOps actualizada", item.Category);
        Assert.Equal("Política seed uno", item.Name);
    }

    [Fact]
    public async Task Put_con_nombre_mayor_a_300_devuelve_400()
    {
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var tooLong = new string('a', 301);
        var res = await client.PutAsync("/policy-catalog/1", Json($"{{\"name\":\"{tooLong}\"}}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid name", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_sin_campos_conocidos_devuelve_400()
    {
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var res = await client.PutAsync("/policy-catalog/1", Json("{\"clave_desconocida\":1}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("No fields to update", body.GetProperty("detail").GetString());
    }

    // ---- Eliminar (soft-delete) ----

    [Fact]
    public async Task Delete_hace_soft_delete_no_borra_la_fila()
    {
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var res = await client.DeleteAsync("/policy-catalog/2");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Política desactivada", body.GetProperty("message").GetString());
        Assert.Equal(2, body.GetProperty("policy_id").GetInt32());

        // La fila sigue existiendo pero inactiva (soft-delete)...
        var item = _factory.Store.Find(2)!;
        Assert.False(item.IsActive);

        // ...y el listado (solo activas) ya no la incluye.
        var items = await client.GetFromJsonAsync<JsonElement>("/policy-catalog");
        Assert.DoesNotContain(items.EnumerateArray(),
            p => p.GetProperty("policy_id").GetInt32() == 2);
    }

    [Fact]
    public async Task Delete_inexistente_devuelve_404()
    {
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var res = await client.DeleteAsync("/policy-catalog/999999");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ---- Seed embebido (linea base BIT de Azure Policy) ----

    [Fact]
    public void Seed_embebido_parsea_con_10_politicas_completas()
    {
        var asm = typeof(PolicyCatalogSchema).Assembly;
        using var stream = asm.GetManifestResourceStream(
            "OptimizacionCostos.Api.Features.PolicyCatalog.Seed.policy_seed.json");
        Assert.NotNull(stream);

        using var doc = JsonDocument.Parse(stream!);
        var policies = doc.RootElement.GetProperty("policies");
        Assert.Equal(10, policies.GetArrayLength());

        foreach (var policy in policies.EnumerateArray())
        {
            foreach (var key in new[] { "name", "recommended_effect", "azure_cli", "powershell" })
            {
                Assert.True(policy.TryGetProperty(key, out var el),
                    $"Falta la clave '{key}' en una política del seed");
                Assert.False(string.IsNullOrWhiteSpace(el.GetString()),
                    $"La clave '{key}' está vacía en la política '{policy.GetProperty("name").GetString()}'");
            }
        }
    }

    // ---- Fixture: API real en memoria, solo se fake-an auth y el store de politicas ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakePolicyCatalogStore Store { get; } = new FakePolicyCatalogStore().Seed();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IPolicyCatalogStore>();
                services.AddSingleton<IPolicyCatalogStore>(Store);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
            });
        }
    }

    /// <summary>Catalogo de politicas en memoria (sin BD) para probar el pipeline HTTP completo.</summary>
    public sealed class FakePolicyCatalogStore : IPolicyCatalogStore
    {
        private readonly List<PolicyItem> _policies = [];
        private int _seq;

        /// <summary>Ultimo diccionario de campos recibido en UpdatePolicyAsync (para exclude_unset).</summary>
        public IReadOnlyDictionary<string, object?>? LastUpdateFields { get; private set; }

        public FakePolicyCatalogStore Seed()
        {
            _policies.Add(new PolicyItem
            {
                PolicyId = ++_seq, PolicyNumber = 1, Name = "Política seed uno",
                Category = "Gobierno", RecommendedEffect = "Deny", IsActive = true,
            });
            _policies.Add(new PolicyItem
            {
                PolicyId = ++_seq, PolicyNumber = 2, Name = "Política seed dos",
                Category = "Seguridad", RecommendedEffect = "Audit", IsActive = true,
            });
            return this;
        }

        public PolicyItem? Find(int policyId) => _policies.FirstOrDefault(p => p.PolicyId == policyId);

        public Task<IReadOnlyList<PolicyItem>> ListPoliciesAsync(bool includeInactive = false, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PolicyItem>>(
                _policies.Where(p => includeInactive || p.IsActive).ToList());

        public Task<PolicyItem?> GetPolicyAsync(int policyId, CancellationToken ct = default)
            => Task.FromResult(Find(policyId));

        public Task<int> CreatePolicyAsync(PolicyCreate data, CancellationToken ct = default)
        {
            var item = new PolicyItem
            {
                PolicyId = ++_seq, Name = data.Name, PolicyNumber = data.PolicyNumber,
                Category = data.Category, IsActive = true,
            };
            _policies.Add(item);
            return Task.FromResult(item.PolicyId);
        }

        public Task<bool> UpdatePolicyAsync(int policyId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
        {
            LastUpdateFields = fields;
            var idx = _policies.FindIndex(p => p.PolicyId == policyId);
            if (idx < 0) return Task.FromResult(false);

            var item = _policies[idx];
            if (fields.TryGetValue("name", out var name) && name is string n) item = item with { Name = n };
            if (fields.TryGetValue("category", out var category)) item = item with { Category = category as string };
            _policies[idx] = item;
            return Task.FromResult(true);
        }

        public Task<bool> SoftDeletePolicyAsync(int policyId, CancellationToken ct = default)
        {
            var idx = _policies.FindIndex(p => p.PolicyId == policyId);
            if (idx < 0) return Task.FromResult(false);
            _policies[idx] = _policies[idx] with { IsActive = false };
            return Task.FromResult(true);
        }
    }
}
