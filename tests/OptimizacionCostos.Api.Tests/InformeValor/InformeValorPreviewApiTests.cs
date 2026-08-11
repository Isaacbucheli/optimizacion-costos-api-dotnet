using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// POST /informe-valor/clients/{clientId}/preview (Tarea 8 de la entrega 2b). Mismo patrón que
/// InformeValorUploadApiTests/InsumosBdRecolectorTests: pipeline MVC real (auth, roles,
/// RequireModule, IAnalysisAccess), solo BD fake.
/// </summary>
public sealed class InformeValorPreviewApiTests : IClassFixture<InformeValorPreviewApiTests.Factory>
{
    private readonly Factory _factory;
    public InformeValorPreviewApiTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string email, string role, bool canEdit)
    {
        _factory.Perms.Set(role, Modules.InformeValor, canView: true, canEdit: canEdit);
        _factory.Service.Invalidate();
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        var token = BitJwt.Create(Factory.Secret, email, "Test User", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object CuerpoValido(string corte = "2026-03-01T00:00:00Z") => new
    {
        period_start = "2026-01-01",
        period_end = "2026-02-28",
        corte,
        meses_parciales_forzados = Array.Empty<string>(),
    };

    [Fact]
    public async Task Sin_acceso_al_cliente_devuelve_403()
    {
        _factory.Access.Deny(clientId: 99);
        var client = ClientFor("p1@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.PostAsJsonAsync("/informe-valor/clients/99/preview", CuerpoValido());

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Con_periodo_invertido_devuelve_400()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("p2@bit.ec", Roles.Consultor, canEdit: false);

        var cuerpo = new
        {
            period_start = "2026-02-28", period_end = "2026-01-01",
            corte = "2026-03-01T00:00:00Z", meses_parciales_forzados = Array.Empty<string>(),
        };
        var res = await client.PostAsJsonAsync("/informe-valor/clients/7/preview", cuerpo);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Con_acceso_devuelve_200_con_el_nombre_del_cliente_y_el_bloque_de_facturacion()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("p3@bit.ec", Roles.Consultor, canEdit: false);

        var body = await client.PostAsJsonAsync("/informe-valor/clients/7/preview", CuerpoValido());
        Assert.Equal(HttpStatusCode.OK, body.StatusCode);

        var json = await body.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Cliente de Prueba", json.GetProperty("meta").GetProperty("cliente").GetString());
        Assert.Equal(1500m, json.GetProperty("fact").GetProperty("total").GetDecimal());
        // Sin casos ni RBAC ni Advisor ni matriz sembrados en este fixture: los otros cuatro
        // bloques viajan null, no un objeto vacío que simule ausencia (mismo contrato que fija
        // InformeValorJsonOptionsTests para InformeValorJsonOptions, acá bajo la política global).
        Assert.Equal(JsonValueKind.Null, json.GetProperty("tickets").ValueKind);
    }

    /// <summary>
    /// Confirma la decisión de serialización de la Tarea 8: /preview usa Ok(modelo) con la
    /// política GLOBAL de Program.cs (<c>DictionaryKeyPolicy = SnakeCaseLower</c>), nunca
    /// InformeValorJsonOptions (claves de diccionario intactas). "catSerie" es un diccionario cuya
    /// clave externa es el nombre de categoría tal cual vino de facturación: bajo la política
    /// global esa clave sale transformada (la prueba unitaria
    /// InformeValorJsonOptionsTests.La_politica_global_del_repo_si_transforma_esa_misma_clave ya
    /// fija que SnakeCaseLower rompe una clave con espacios), y bajo InformeValorJsonOptions
    /// (D13) tendría que sobrevivir intacta. Si /preview usara por error InformeValorJsonOptions,
    /// este test lo detectaría: la clave original seguiría apareciendo tal cual.
    /// </summary>
    [Fact]
    public async Task Usa_la_politica_global_de_serializacion_no_InformeValorJsonOptions()
    {
        _factory.Access.Allow(clientId: 8);
        var client = ClientFor("p4@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.PostAsJsonAsync("/informe-valor/clients/8/preview", CuerpoValido());
        var texto = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain("\"Redes y Conectividad\"", texto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sin_nombre_de_cliente_resuelto_usa_un_rotulo_por_id()
    {
        _factory.Access.Allow(clientId: 55);
        var client = ClientFor("p5@bit.ec", Roles.Consultor, canEdit: false);

        var body = await client.PostAsJsonAsync("/informe-valor/clients/55/preview", CuerpoValido());
        var json = await body.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Cliente 55", json.GetProperty("meta").GetProperty("cliente").GetString());
    }

    // ---- Fixture: API real en memoria, solo se fake-an auth/acceso/permisos y las tres fuentes ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeAnalysisAccessForPreview Access { get; } = new();
        public FakeInformeValorStoreConDatos Store { get; } = new();
        public FakeInsumosBdRecolectorVacio Recolector { get; } = new();
        public FakeClientStore ClientStore { get; } = new();
        public FakeModulePermissionStore Perms { get; } = new FakeModulePermissionStore().SeedDefaults();
        public IModulePermissionService Service => Services.GetRequiredService<IModulePermissionService>();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IAnalysisAccess>();
                services.AddSingleton<IAnalysisAccess>(Access);
                services.RemoveAll<IInformeValorStore>();
                services.AddSingleton<IInformeValorStore>(Store);
                services.RemoveAll<IInsumosBdRecolector>();
                services.AddSingleton<IInsumosBdRecolector>(Recolector);
                services.RemoveAll<IClientStore>();
                services.AddSingleton<IClientStore>(ClientStore);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(Perms);
                // El servicio cacheado debe ser singleton en tests para poder invalidarlo desde
                // fuera del scope del request.
                services.RemoveAll<IModulePermissionService>();
                services.AddSingleton<IModulePermissionService, ModulePermissionService>();
            });
        }
    }

    /// <summary>Copiado de InformeValorUploadApiTests/InsumosBdRecolectorTests a propósito: cada
    /// archivo de test es dueño de sus propias clases anidadas.</summary>
    public sealed class FakeAnalysisAccessForPreview : IAnalysisAccess
    {
        private readonly HashSet<int> _allowed = [];

        public void Allow(int clientId) => _allowed.Add(clientId);
        public void Deny(int clientId) => _allowed.Remove(clientId);

        public Task<IReadOnlySet<int>?> AccessibleClientIdsAsync(ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<int>?>(_allowed);

        public Task<AccessCheck> AssertAnalysisAccessAsync(ClaimsPrincipal user, int analysisId, CancellationToken ct = default)
            => Task.FromResult(AccessCheck.NotFound("no aplica"));

        public Task<AccessCheck> AssertCostResultAccessAsync(ClaimsPrincipal user, int costResultId, CancellationToken ct = default)
            => Task.FromResult(AccessCheck.NotFound("no aplica"));

        public Task<AccessCheck> AssertClientAccessAsync(ClaimsPrincipal user, int clientId, CancellationToken ct = default)
            => Task.FromResult(_allowed.Contains(clientId) ? AccessCheck.Allow() : AccessCheck.Forbidden());

        public Task<AccessCheck> AssertFileAccessAsync(ClaimsPrincipal user, int fileId, CancellationToken ct = default)
            => Task.FromResult(AccessCheck.NotFound("no aplica"));
    }

    /// <summary>Dos filas de facturación en el período del cuerpo válido (enero/febrero 2026, sub-1),
    /// para que <c>fact</c> no salga null y el endpoint tenga algo real que calcular. Sin casos, sin
    /// RBAC vía Excel: ningún test de este archivo los necesita.</summary>
    public sealed class FakeInformeValorStoreConDatos : IInformeValorStore
    {
        public Task<int> ReplaceFacturacionAsync(
            int clientId, string fileName, string? user, ParseResult<FacturacionRow> parsed, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> ReplaceCasosAsync(
            int clientId, string fileName, string? user, ParseResult<CasoRow> parsed, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> ReplaceRbacAsync(
            int clientId, string fileName, string? user, RbacParseResult parsed, CancellationToken ct)
            => throw new NotSupportedException();

        public Task DeleteInsumoAsync(int clientId, string kind, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<InsumoEstado>>([]);

        public Task<IReadOnlyList<FacturacionRow>> GetFacturacionAsync(int clientId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FacturacionRow>>(
            [
                new FacturacionRow(
                    Hash: "h1", Tenant: null, SubscriptionName: "Suscripción Uno", SubscriptionId: "sub-1",
                    ResourceGroup: "rg-1", ResourceName: "vm-1", CostCenter: null, Category: "Redes y Conectividad",
                    Subcategory: null, Service: null, Quantity: null, Unit: null, Rate: null,
                    Pvp: 1000m, Year: 2026, Month: 1),
                new FacturacionRow(
                    Hash: "h2", Tenant: null, SubscriptionName: "Suscripción Uno", SubscriptionId: "sub-1",
                    ResourceGroup: "rg-1", ResourceName: "vm-1", CostCenter: null, Category: "Redes y Conectividad",
                    Subcategory: null, Service: null, Quantity: null, Unit: null, Rate: null,
                    Pvp: 500m, Year: 2026, Month: 2),
            ]);

        public Task<IReadOnlyList<CasoRow>> GetCasosAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CasoRow>>([]);

        public Task<IReadOnlyList<RbacFila>> GetRbacAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RbacFila>>([]);
    }

    public sealed class FakeInsumosBdRecolectorVacio : IInsumosBdRecolector
    {
        public Task<InsumosBd> LeerAsync(int clientId, CancellationToken ct = default) => Task.FromResult(new InsumosBd(
            Advisor: [], Matriz: [], Rbac: [], Retiros: [],
            EstadoRbac: new EstadoRbacResultado(
                DisponibilidadRbac.NoDisponible, new EjesRbac(false, false), null, "Sin datos de prueba."),
            SeguridadGestionadaExternamente: false, SeguridadGestionadaNota: null,
            LeidoEn: new DateTime(2026, 1, 1)));

        public Task<EstadoRbacResultado> LeerEstadoRbacAsync(int clientId, CancellationToken ct = default) =>
            Task.FromResult(new EstadoRbacResultado(
                DisponibilidadRbac.NoDisponible, new EjesRbac(false, false), null, "Sin datos de prueba."));
    }

    /// <summary>Nombre por cliente en memoria; sin entrada, <c>GetNameAsync</c> devuelve null (mismo
    /// contrato que SqlClientStore para un client_id sin fila), y el controller cae al rótulo por
    /// id.</summary>
    public sealed class FakeClientStore : IClientStore
    {
        public Dictionary<int, string> Nombres { get; } = new() { [7] = "Cliente de Prueba" };

        public Task<string?> GetNameAsync(int clientId, CancellationToken ct = default) =>
            Task.FromResult(Nombres.GetValueOrDefault(clientId));

        public Task<IReadOnlyList<ClientListItem>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> CreateAsync(string clientName, string? taxId, string? contactName, string? contactEmail, string? notes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> NameExistsAsync(string name, int excludeClientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RenameAsync(int clientId, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string Name, string? LogoBlobName)?> GetNameAndLogoAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, int>> PurgeDataAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(IReadOnlyDictionary<string, int> Counts, string? LogoBlobName)> DeleteClientCascadeAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateLogoMetaAsync(int clientId, string blobName, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string? BlobName, string? ContentType)?> GetLogoAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearLogoAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(bool Managed, string? Note)> GetSecurityManagementAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSecurityManagementAsync(int clientId, bool managed, string? note, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
