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
using OptimizacionCostos.Api.Features.Cdc;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;
using OptimizacionCostos.Api.Features.InformeValor.Entrega;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// POST /informe-valor/clients/{clientId}/insumos/rbac: kind=rbac (Tarea de la entrega 2) más la
/// decisión 4 del brief ("precedencia: gana la base") — si la base ya tiene el insumo de RBAC
/// completo, el archivo se descarta y el módulo avisa en la respuesta en vez de callar en
/// silencio. Mismo patrón de fixture que InformeValorUploadApiTests/InformeValorPreviewApiTests:
/// pipeline MVC real, solo BD fake.
/// </summary>
public sealed class InformeValorRbacUploadApiTests : IClassFixture<InformeValorRbacUploadApiTests.Factory>
{
    private readonly Factory _factory;
    public InformeValorRbacUploadApiTests(Factory factory) => _factory = factory;

    private static readonly string?[] Cabecera =
    [
        "Suscripción", "Scope", "Nivel", "Rol", "Clase de rol", "Rol personalizado", "Tipo",
        "Nombre", "Correo / Login", "Tipo usuario", "Vía grupo", "Cuenta activa", "Último login", "MFA",
    ];

    private static readonly string?[] Fila =
        ["Sub Uno", "/subscriptions/s1", "subscription", "Contributor", "", "", "User",
         "Ana Perez", "ana@x.com", "Member", "", "Sí", "2026-01-05 10:00", ""];

    private static byte[] ArchivoRbacValido()
    {
        using var ms = XlsxRowReaderTests.BuildXlsx([Cabecera, Fila], sheetName: "Asignaciones RBAC");
        return ms.ToArray();
    }

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

    private static MultipartFormDataContent Multipart(string fileName, byte[] bytes)
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(part, "file", fileName);
        return content;
    }

    /// <summary>Decisión 4: con la base en "Completo", el archivo se descarta -- pero NUNCA en
    /// silencio. La respuesta lo dice explícito y el store nunca llega a persistir nada.</summary>
    [Fact]
    public async Task Con_la_base_completa_el_archivo_se_descarta_y_se_avisa()
    {
        _factory.Access.Allow(clientId: 7);
        _factory.Recolector.Disponibilidad = DisponibilidadRbac.Completo;
        var client = ClientFor("r1@bit.ec", Roles.Consultor, canEdit: true);

        using var body = Multipart("rbac.xlsx", ArchivoRbacValido());
        var res = await client.PostAsync("/informe-valor/clients/7/insumos/rbac", body);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("descartado").GetBoolean());
        Assert.False(_factory.Store.ReplaceRbacLlamado);
    }

    [Theory]
    [InlineData("ParcialFaltaIdentidad")]
    [InlineData("NoDisponible")]
    public async Task Sin_la_base_completa_el_archivo_se_persiste(string disponibilidad)
    {
        _factory.Access.Allow(clientId: 7);
        _factory.Recolector.Disponibilidad = Enum.Parse<DisponibilidadRbac>(disponibilidad);
        var client = ClientFor($"r2-{disponibilidad}@bit.ec", Roles.Consultor, canEdit: true);

        using var body = Multipart("rbac.xlsx", ArchivoRbacValido());
        var res = await client.PostAsync("/informe-valor/clients/7/insumos/rbac", body);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("descartado").GetBoolean());
        Assert.Equal(1, json.GetProperty("rows_processed").GetInt32());
        Assert.True(_factory.Store.ReplaceRbacLlamado);
    }

    [Fact]
    public async Task Un_archivo_con_forma_inesperada_devuelve_400()
    {
        _factory.Access.Allow(clientId: 7);
        _factory.Recolector.Disponibilidad = DisponibilidadRbac.NoDisponible;
        var client = ClientFor("r3@bit.ec", Roles.Consultor, canEdit: true);

        using var xlsx = XlsxRowReaderTests.BuildXlsx([["Uno", "Dos"], ["a", "b"]]);
        using var body = Multipart("rbac.xlsx", xlsx.ToArray());
        var res = await client.PostAsync("/informe-valor/clients/7/insumos/rbac", body);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- Fixture: API real en memoria, solo se fake-an auth/acceso/permisos y las dos fuentes ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeAnalysisAccessForRbacUpload Access { get; } = new();
        public FakeInformeValorStoreParaRbac Store { get; } = new();
        public FakeInsumosBdRecolectorConEstado Recolector { get; } = new();
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
                // Entrega 2d: el controller también pide IReservationService/IAzureReservationsClient
                // (la foto de reservas de /preview/variacion-consumo). Ningún test de esta clase pega
                // a esa ruta (todos van a /insumos/rbac), pero sin este reemplazo el controller se
                // construiria con las implementaciones reales.
                services.RemoveAll<IReservationService>();
                services.AddSingleton<IReservationService>(new FakeReservationServiceNoUsado());
                services.RemoveAll<IAzureReservationsClient>();
                services.AddSingleton<IAzureReservationsClient>(new FakeAzureReservationsClientNoUsado());
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(Perms);
                services.RemoveAll<IModulePermissionService>();
                services.AddSingleton<IModulePermissionService, ModulePermissionService>();
            });
        }
    }

    public sealed class FakeAnalysisAccessForRbacUpload : IAnalysisAccess
    {
        private readonly HashSet<int> _allowed = [];
        public void Allow(int clientId) => _allowed.Add(clientId);

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

    /// <summary>Recolector fake cuya única variable de prueba es la disponibilidad de RBAC: el
    /// resto de InsumosBd no le importa a ningún test de esta clase. El controller solo llama a
    /// <see cref="LeerEstadoRbacAsync"/> en la ruta de Subir(kind=rbac) (el método liviano), así
    /// que los dos métodos comparten la misma disponibilidad configurable.</summary>
    public sealed class FakeInsumosBdRecolectorConEstado : IInsumosBdRecolector
    {
        public DisponibilidadRbac Disponibilidad { get; set; } = DisponibilidadRbac.NoDisponible;

        public Task<InsumosBd> LeerAsync(int clientId, CancellationToken ct = default) => Task.FromResult(new InsumosBd(
            Advisor: [], Matriz: [], Rbac: [], Retiros: [],
            EstadoRbac: new EstadoRbacResultado(Disponibilidad, new EjesRbac(false, false), null, "prueba"),
            SeguridadGestionadaExternamente: false, SeguridadGestionadaNota: null,
            LeidoEn: new DateTime(2026, 1, 1)));

        public Task<EstadoRbacResultado> LeerEstadoRbacAsync(int clientId, CancellationToken ct = default) =>
            Task.FromResult(new EstadoRbacResultado(Disponibilidad, new EjesRbac(false, false), null, "prueba"));

        // Ningún test de este archivo pega a /estado (todos van a Subir/Borrar de rbac): revienta
        // a propósito si algo llega a llamarlo, mismo criterio que el resto de esta clase.
        public Task<(EstadoRbacResultado Estado, string? Origen)> LeerEstadoRbacConOrigenAsync(
            int clientId, CancellationToken ct = default) => throw new NotSupportedException();

        // Ídem: /preview/variacion-consumo es el único que lo llama, y no se toca acá.
        public Task<IReadOnlyList<HallazgoResueltoFila>> LeerHallazgosResueltosAsync(
            int clientId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Store fake que registra si ReplaceRbacAsync llegó a llamarse: es la comprobación
    /// central de la decisión 4 (con la base completa, el archivo NUNCA se persiste).</summary>
    public sealed class FakeInformeValorStoreParaRbac : IInformeValorStore
    {
        public bool ReplaceRbacLlamado { get; private set; }

        public Task<int> ReplaceFacturacionAsync(
            int clientId, string fileName, string? user, ParseResult<FacturacionRow> parsed, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> ReplaceCasosAsync(
            int clientId, string fileName, string? user, ParseResult<CasoRow> parsed, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> ReplaceRbacAsync(
            int clientId, string fileName, string? user, RbacParseResult parsed, CancellationToken ct)
        {
            ReplaceRbacLlamado = true;
            return Task.FromResult(1);
        }

        public Task DeleteInsumoAsync(int clientId, string kind, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<InsumoEstado>>([]);

        public Task<IReadOnlyList<FacturacionRow>> GetFacturacionAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CasoRow>> GetCasosAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RbacFila>> GetRbacAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();

        // Entrega 3, F4: la bitacora de entregas no la ejercita ningun test de esta clase. Revienta
        // en vez de devolver vacio: un archivo de entregas silenciosamente vacio es justo el cero
        // ambiguo que este modulo saca de todos lados.
        public Task<int> RegistrarEntregaAsync(EntregaNueva entrega, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<EntregaResumen>> GetEntregasAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<EntregaArchivada?> GetEntregaAsync(int clientId, int entregaId, CancellationToken ct)
            => throw new NotSupportedException();

    }

    /// <summary>Ningún test de esta clase pega a /preview/variacion-consumo: revienta a propósito si
    /// algo llega a llamarlo, mismo criterio que
    /// FakeInsumosBdRecolectorConEstado.LeerEstadoRbacConOrigenAsync.</summary>
    public sealed class FakeReservationServiceNoUsado : IReservationService
    {
        public Task<IReadOnlyList<CredentialRef>> ActiveCredentialsAsync(int clientId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(IReadOnlyList<ReservationDto> Reservations, IReadOnlyList<object> Errors)> FetchAllAsync(
            IReadOnlyList<CredentialRef> credentials, int alertDays, bool includeUtilization, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, object?>> ListClientReservationsAsync(
            IReadOnlyList<CredentialRef> credentials, int alertDays, bool includeUtilization, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>Mismo criterio que <see cref="FakeReservationServiceNoUsado"/>.</summary>
    public sealed class FakeAzureReservationsClientNoUsado : IAzureReservationsClient
    {
        public Task<IReadOnlyList<ReservationDto>> FetchForCredentialAsync(
            int credentialId, int alertDays, DateOnly today, bool includeUtilization, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(string Last, string Avg7d)> GetUtilizationAsync(int credentialId, string reservationId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ReservationConsumer>> GetConsumersAsync(int credentialId, string reservationId, int days, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
