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
using OptimizacionCostos.Api.Features.Cdc;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Entrega;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// El CRUD del registro manual de acciones ejecutadas (entrega 8, pieza B):
/// GET/POST/PUT/DELETE /informe-valor/clients/{clientId}/acciones. Pipeline MVC real (auth,
/// roles, RequireModule, IAnalysisAccess), solo BD fake — mismo patrón que
/// InformeValorUploadApiTests, con un store con estado para poder verificar lo persistido.
/// </summary>
public sealed class InformeValorAccionesApiTests : IClassFixture<InformeValorAccionesApiTests.Factory>
{
    private readonly Factory _factory;
    public InformeValorAccionesApiTests(Factory factory) => _factory = factory;

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

    /// <summary>El MVC del proyecto bindea con SnakeCaseLower (Program.cs), así que el cuerpo va
    /// en snake_case — igual que lo mandará el front.</summary>
    private static object Cuerpo(
        string? oportunidad = "Apagado de VMs de desarrollo", string? mes = "2026-07",
        string? fin = null, decimal? monto = 450m, string? nota = null, string? evidencia = null) =>
        new Dictionary<string, object?>
        {
            ["oportunidad"] = oportunidad,
            ["categoria"] = "VMs (right-size / apagado)",
            ["mes_ejecucion"] = mes,
            ["mes_fin"] = fin,
            ["monto_mensual"] = monto,
            ["recurso"] = "vm-dev-01",
            ["nota"] = nota,
            ["evidencia"] = evidencia,
        };

    [Fact]
    public async Task Post_valido_crea_y_devuelve_201_con_id()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("a1@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync("/informe-valor/clients/7/acciones", Cuerpo());

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("accion_id").GetInt32() > 0);
        Assert.Contains(_factory.Store.Acciones, a => a.Oportunidad == "Apagado de VMs de desarrollo");
    }

    [Fact]
    public async Task Post_sin_oportunidad_es_400()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("a2@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync("/informe-valor/clients/7/acciones", Cuerpo(oportunidad: "  "));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("oportunidad", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-7")]
    [InlineData("julio 2026")]
    [InlineData(null)]
    public async Task Post_con_mes_invalido_es_400(string? mes)
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("a3@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync("/informe-valor/clients/7/acciones", Cuerpo(mes: mes));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Post_con_fin_anterior_al_inicio_es_400()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("a4@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync(
            "/informe-valor/clients/7/acciones", Cuerpo(mes: "2026-05", fin: "2026-01"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Post_con_monto_negativo_es_400()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("a5@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync("/informe-valor/clients/7/acciones", Cuerpo(monto: -1m));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Get_devuelve_las_acciones_del_cliente()
    {
        _factory.Access.Allow(clientId: 8);
        _factory.Store.Sembrar(8, new AccionManualNueva(
            "Reducción de plan App Service", null, "2026-06", null, null, null, null, null));
        var client = ClientFor("a6@bit.ec", Roles.Lector, canEdit: false);

        var res = await client.GetAsync("/informe-valor/clients/8/acciones");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Contains(json.RootElement.GetProperty("acciones").EnumerateArray(),
            a => a.GetProperty("oportunidad").GetString() == "Reducción de plan App Service");
    }

    [Fact]
    public async Task Delete_de_id_ajeno_es_404()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("a7@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.DeleteAsync("/informe-valor/clients/7/acciones/99999");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Post_sin_permiso_edit_es_403()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("a8@bit.ec", Roles.Lector, canEdit: false);

        var res = await client.PostAsJsonAsync("/informe-valor/clients/7/acciones", Cuerpo());

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Post_sin_acceso_al_cliente_es_403()
    {
        _factory.Access.Deny(clientId: 66);
        var client = ClientFor("a9@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PostAsJsonAsync("/informe-valor/clients/66/acciones", Cuerpo());

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Put_actualiza_y_devuelve_204()
    {
        _factory.Access.Allow(clientId: 9);
        var id = _factory.Store.Sembrar(9, new AccionManualNueva(
            "Apagado original", null, "2026-03", null, 100m, null, null, null));
        var client = ClientFor("a10@bit.ec", Roles.Consultor, canEdit: true);

        var res = await client.PutAsJsonAsync($"/informe-valor/clients/9/acciones/{id}",
            Cuerpo(oportunidad: "Apagado corregido", mes: "2026-04"));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Contains(_factory.Store.Acciones, a => a.Oportunidad == "Apagado corregido");
    }

    // ---- Fixture ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public InformeValorUploadApiTests.FakeAnalysisAccessForInformeValor Access { get; } = new();
        public FakeAccionesStore Store { get; } = new();
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
                services.AddSingleton<IInsumosBdRecolector>(new InformeValorUploadApiTests.FakeInsumosBdRecolectorVacio());
                services.RemoveAll<IReservationService>();
                services.AddSingleton<IReservationService>(new InformeValorUploadApiTests.FakeReservationServiceNoUsado());
                services.RemoveAll<IAzureReservationsClient>();
                services.AddSingleton<IAzureReservationsClient>(new InformeValorUploadApiTests.FakeAzureReservationsClientNoUsado());
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(Perms);
                services.RemoveAll<IModulePermissionService>();
                services.AddSingleton<IModulePermissionService, ModulePermissionService>();
            });
        }
    }

    /// <summary>Store en memoria SOLO para el CRUD de acciones: el resto de la interfaz lanza,
    /// para que un endpoint que toque otra cosa se vea en el acto.</summary>
    public sealed class FakeAccionesStore : IInformeValorStore
    {
        private readonly Dictionary<int, (int ClientId, AccionManualNueva Datos, bool Activo)> _filas = [];
        private int _siguiente;

        public IReadOnlyList<AccionManualNueva> Acciones =>
            _filas.Values.Where(f => f.Activo).Select(f => f.Datos).ToList();

        public int Sembrar(int clientId, AccionManualNueva accion)
        {
            var id = ++_siguiente;
            _filas[id] = (clientId, accion, true);
            return id;
        }

        public Task<IReadOnlyList<AccionManualRow>> GetAccionesManualesAsync(int clientId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AccionManualRow>>(_filas
                .Where(kv => kv.Value.ClientId == clientId && kv.Value.Activo)
                .Select(kv => new AccionManualRow(
                    kv.Key, kv.Value.Datos.Oportunidad, kv.Value.Datos.Categoria,
                    kv.Value.Datos.MesEjecucion, kv.Value.Datos.MesFin, kv.Value.Datos.MontoMensual,
                    kv.Value.Datos.Recurso, kv.Value.Datos.Nota, kv.Value.Datos.Evidencia,
                    "test@bit.ec", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)))
                .ToList());

        public Task<int> InsertAccionManualAsync(int clientId, AccionManualNueva accion, string? user, CancellationToken ct) =>
            Task.FromResult(Sembrar(clientId, accion));

        public Task<bool> UpdateAccionManualAsync(int clientId, int accionId, AccionManualNueva accion, CancellationToken ct)
        {
            if (!_filas.TryGetValue(accionId, out var fila) || fila.ClientId != clientId || !fila.Activo)
                return Task.FromResult(false);
            _filas[accionId] = (clientId, accion, true);
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAccionManualAsync(int clientId, int accionId, CancellationToken ct)
        {
            if (!_filas.TryGetValue(accionId, out var fila) || fila.ClientId != clientId || !fila.Activo)
                return Task.FromResult(false);
            _filas[accionId] = (fila.ClientId, fila.Datos, false);
            return Task.FromResult(true);
        }

        public Task<int> ReplaceFacturacionAsync(int clientId, string fileName, string? user, ParseResult<FacturacionRow> parsed, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<int> ReplaceCasosAsync(int clientId, string fileName, string? user, ParseResult<CasoRow> parsed, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<int> ReplaceRbacAsync(int clientId, string fileName, string? user, RbacParseResult parsed, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<int> ReplaceEvolucionAsync(int clientId, string fileName, string? user, ParseResult<EvolucionRow> parsed, CancellationToken ct)
            => throw new NotSupportedException();
        public Task DeleteInsumoAsync(int clientId, string kind, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<CoberturaMeses> GetCoberturaMesesAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<FacturacionRow>> GetFacturacionAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<CasoRow>> GetCasosAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<RbacFila>> GetRbacAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<EvolucionRow>> GetEvolucionAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<int> RegistrarEntregaAsync(EntregaNueva entrega, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<EntregaResumen>> GetEntregasAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<EntregaArchivada?> GetEntregaAsync(int clientId, int entregaId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
