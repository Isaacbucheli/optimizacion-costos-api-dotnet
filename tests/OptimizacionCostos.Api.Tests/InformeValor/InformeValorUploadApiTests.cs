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
/// Comportamiento del orden de validaciones en POST /informe-valor/clients/{clientId}/insumos/{kind}:
/// pipeline MVC real (auth, roles, RequireModule, IAnalysisAccess), solo BD fake. Espejo de
/// BoletinNovedadClienteApiTests. Cubre las dos propiedades que motivaron la corrección del brief
/// original (ver InformeValorController.Subir) más un tercer defecto de la misma familia que
/// apareció al revisar esa corrección:
///   1) el guard de acceso por cliente corre ANTES que la validación de extensión (si no, un
///      usuario sin permiso recibe un error distinto según cómo se llame su archivo: fuga de
///      información por el nombre).
///   2) un archivo sobre el tope del módulo devuelve 413 limpio (el chequeo manual de
///      Request.ContentLength en Subir()). La protección contra que un BadHttpRequestException
///      del model binding escape como 500 descansa en el controller, no en este test: detalle en
///      el comentario del método de abajo (TestServer no reproduce ese escape).
///   3) un content-type que no es multipart/form-data devuelve 400 limpio y no el mismo 500 opaco,
///      por la misma razón (InvalidOperationException de ReadFormAsync sin capturar).
/// </summary>
public sealed class InformeValorUploadApiTests : IClassFixture<InformeValorUploadApiTests.Factory>
{
    private readonly Factory _factory;
    public InformeValorUploadApiTests(Factory factory) => _factory = factory;

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

    /// <summary>
    /// El guard de acceso por cliente va ANTES de validar la extensión. Con el orden inverso
    /// (el que tiene hoy WafController.UploadIngestion) el error que recibe un usuario sin permiso
    /// depende de cómo se llame su archivo, que es una fuga de información por el nombre.
    /// </summary>
    [Fact]
    public async Task Sin_acceso_al_cliente_la_extension_invalida_igual_devuelve_403()
    {
        _factory.Access.Deny(clientId: 99);
        var client = ClientFor("c1@bit.ec", Roles.Consultor, canEdit: true);

        using var body = Multipart("cualquier-cosa.txt", new byte[] { 1, 2, 3 });
        var res = await client.PostAsync("/informe-valor/clients/99/insumos/facturacion", body);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    /// <summary>
    /// Fija el chequeo manual de Request.ContentLength en Subir(): con un archivo declarado por
    /// encima del tope del módulo, el controller devuelve 413 antes de tocar el cuerpo.
    ///
    /// Ojo: esto NO prueba que la excepción del model binding no escape como 500 bajo Kestrel
    /// real. TestServer no aplica IHttpMaxRequestBodySizeFeature, así que este mismo test pasa
    /// igual con la firma original del brief (IFormFile como parámetro más [RequestSizeLimit],
    /// sin DisableFormValueModelBinding); lo comprobé a mano durante la Tarea 8, revirtiendo la
    /// firma. La protección contra ese escape descansa en [DisableFormValueModelBinding] más
    /// [RequestSizeLimit] del controller (ver su comentario de clase), no en un test de este
    /// repo: haría falta correr contra Kestrel real, no contra WebApplicationFactory, y eso no
    /// se hizo.
    /// </summary>
    [Fact]
    public async Task Un_archivo_sobre_el_tope_devuelve_413_y_no_500()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("c2@bit.ec", Roles.Consultor, canEdit: true);

        // 33 MB: por encima del tope del módulo (32 MB).
        using var body = Multipart("bitcost.xlsx", new byte[33 * 1024 * 1024]);
        var res = await client.PostAsync("/informe-valor/clients/7/insumos/facturacion", body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    /// <summary>Con acceso y tamaño válidos, la extensión sí se valida.</summary>
    [Fact]
    public async Task Con_acceso_una_extension_invalida_devuelve_400()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("c3@bit.ec", Roles.Consultor, canEdit: true);

        using var body = Multipart("bitcost.txt", new byte[] { 1, 2, 3 });
        var res = await client.PostAsync("/informe-valor/clients/7/insumos/facturacion", body);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    /// <summary>
    /// Un cuerpo que no es multipart/form-data (ej. JSON) hace que Request.ReadFormAsync lance
    /// InvalidOperationException ("Incorrect Content-Type"). Sin capturarla, esa excepción se
    /// escapa igual que la del tope de tamaño: llega sin manejar al middleware de última
    /// instancia y sale como 500 opaco. Es la misma clase de defecto que motivó esta tarea,
    /// solo que disparado por el content-type en vez del tamaño.
    /// </summary>
    [Fact]
    public async Task Con_content_type_no_multipart_devuelve_400_y_no_500()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("c4@bit.ec", Roles.Consultor, canEdit: true);

        using var body = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var res = await client.PostAsync("/informe-valor/clients/7/insumos/facturacion", body);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- kind=evolucion (Tarea 4 de la entrega 5): mismo despacho que facturación, con parser y
    // replace propios. Layout del pivot copiado de EvolucionParserTests (FilaAnios/FilaMeses/
    // FilaCabecera + una fila de datos con dos meses con valor). ----

    private static readonly string[] EvolucionFilaAnios =
        ["Jerarquía de Fechas - Año", "", "", "2025", "2025", "2025", "2026", "2026", "2026", "Total"];
    private static readonly string[] EvolucionFilaMeses =
        ["Jerarquía de Fechas - Mes", "", "", " Noviembre", " Diciembre", "Total", " Enero", " Febrero", "Total", ""];
    private static readonly string[] EvolucionFilaCabecera =
        ["Categoría", "Subcategoría", "Recurso", "PvP", "PvP", "PvP", "PvP", "PvP", "PvP", "PvP"];
    private static readonly string[] EvolucionFilaDatos =
        ["Storage", "Disks", "disco-1", "10.5", "", "99", "20.25", "", "99", "99"];

    private static byte[] ArchivoEvolucionValido()
    {
        using var ms = XlsxRowReaderTests.BuildXlsx(
            [EvolucionFilaAnios, EvolucionFilaMeses, EvolucionFilaCabecera, EvolucionFilaDatos]);
        return ms.ToArray();
    }

    /// <summary>El kind nuevo entra por la misma puerta: parser propio, replace propio.</summary>
    [Fact]
    public async Task Subir_evolucion_persiste_las_filas_del_pivot()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("evo1@bit.ec", Roles.Consultor, canEdit: true);

        using var body = Multipart("evolucion.xlsx", ArchivoEvolucionValido());
        var res = await client.PostAsync("/informe-valor/clients/7/insumos/evolucion", body);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("rows_processed").GetInt32() > 0);
        Assert.NotEmpty(_factory.Store.EvolucionGuardada);
    }

    /// <summary>La tarjeta del front se dibuja desde acá: obligatoria y con su estado.</summary>
    [Fact]
    public async Task El_estado_lista_evolucion_como_obligatoria()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("evo2@bit.ec", Roles.Consultor, canEdit: false);

        var body = await client.GetFromJsonAsync<JsonElement>("/informe-valor/clients/7/estado");

        var evolucion = Assert.Single(
            body.GetProperty("insumos").EnumerateArray(),
            i => i.GetProperty("kind").GetString() == "evolucion");
        Assert.True(evolucion.GetProperty("obligatorio").GetBoolean());
    }

    private static MultipartFormDataContent Multipart(string fileName, byte[] bytes)
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(part, "file", fileName);
        return content;
    }

    // ---- Fixture: API real en memoria, solo se fake-an auth/acceso/permisos y el store de insumos ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeAnalysisAccessForInformeValor Access { get; } = new();
        public FakeInformeValorStore Store { get; } = new();
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
                // El controller también pide IInsumosBdRecolector (Tarea 7, endpoint de
                // diagnóstico). Ningún test de esta clase lo ejercita: un falso que revienta si se
                // llega a llamar mantiene la fixture "solo BD fake" en vez de depender en silencio
                // de que SqlInsumosBdRecolector no abra conexión al construirse.
                services.RemoveAll<IInsumosBdRecolector>();
                services.AddSingleton<IInsumosBdRecolector>(new FakeInsumosBdRecolectorVacio());
                // Entrega 2d: el controller también pide IReservationService/IAzureReservationsClient
                // (la foto de reservas de /preview/variacion-consumo). Ningún test de esta clase pega
                // a esa ruta (todos van a /insumos/{kind}), pero sin este reemplazo el controller se
                // construiria con las implementaciones reales (SQL/Azure de verdad) -- rompe "solo BD
                // fake" en silencio, mismo motivo que el recolector de arriba.
                services.RemoveAll<IReservationService>();
                services.AddSingleton<IReservationService>(new FakeReservationServiceNoUsado());
                services.RemoveAll<IAzureReservationsClient>();
                services.AddSingleton<IAzureReservationsClient>(new FakeAzureReservationsClientNoUsado());
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(Perms);
                // El servicio cacheado debe ser singleton en tests para poder invalidarlo
                // desde fuera del scope del request.
                services.RemoveAll<IModulePermissionService>();
                services.AddSingleton<IModulePermissionService, ModulePermissionService>();
            });
        }
    }

    /// <summary>Acceso en memoria: solo los client_id agregados con Allow() son accesibles. Empieza
    /// vacío (deny por defecto, igual que un consultor sin asignaciones en dbo.user_client_assignment).</summary>
    public sealed class FakeAnalysisAccessForInformeValor : IAnalysisAccess
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

    /// <summary>Store de insumos en memoria (sin SQL): no hace nada, solo permite que el controller
    /// se construya. Ningún test de esta clase llega a invocar sus métodos (todos resuelven en el
    /// guard de acceso, el tope de tamaño, el content-type o la extensión, antes de tocar el store).</summary>
    public sealed class FakeInformeValorStore : IInformeValorStore
    {
        /// <summary>Lo que llegó a ReplaceEvolucionAsync: la comprobación de
        /// Subir_evolucion_persiste_las_filas_del_pivot de que el kind nuevo llegó hasta el store,
        /// no solo que el parser corrió.</summary>
        public List<EvolucionRow> EvolucionGuardada { get; } = [];

        public Task<int> ReplaceFacturacionAsync(
            int clientId, string fileName, string? user, ParseResult<FacturacionRow> parsed, CancellationToken ct)
            => Task.FromResult(1);

        public Task<int> ReplaceCasosAsync(
            int clientId, string fileName, string? user, ParseResult<CasoRow> parsed, CancellationToken ct)
            => Task.FromResult(1);

        public Task<int> ReplaceRbacAsync(
            int clientId, string fileName, string? user, RbacParseResult parsed, CancellationToken ct)
            => Task.FromResult(1);

        public Task<int> ReplaceEvolucionAsync(
            int clientId, string fileName, string? user, ParseResult<EvolucionRow> parsed, CancellationToken ct)
        {
            EvolucionGuardada.AddRange(parsed.Rows);
            return Task.FromResult(1);
        }

        public Task DeleteInsumoAsync(int clientId, string kind, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<InsumoEstado>>([]);

        public Task<IReadOnlyList<FacturacionRow>> GetFacturacionAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FacturacionRow>>([]);

        public Task<IReadOnlyList<CasoRow>> GetCasosAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CasoRow>>([]);

        public Task<IReadOnlyList<RbacFila>> GetRbacAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RbacFila>>([]);

        public Task<IReadOnlyList<EvolucionRow>> GetEvolucionAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EvolucionRow>>([]);

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

    /// <summary>Ensamblador falso que revienta si se lo llega a usar, salvo
    /// LeerEstadoRbacConOrigenAsync: El_estado_lista_evolucion_como_obligatoria pega a /estado, y
    /// InformeValorController.Estado() llama a ese método para el bloque estado_rbac. El resto
    /// sigue reventando -- ver el comentario de ConfigureWebHost sobre por qué está acá aunque
    /// nada de esta clase lo necesite.</summary>
    public sealed class FakeInsumosBdRecolectorVacio : IInsumosBdRecolector
    {
        public Task<InsumosBd> LeerAsync(int clientId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<EstadoRbacResultado> LeerEstadoRbacAsync(int clientId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(EstadoRbacResultado Estado, string? Origen)> LeerEstadoRbacConOrigenAsync(
            int clientId, CancellationToken ct = default) => Task.FromResult<(EstadoRbacResultado, string?)>(
                (new EstadoRbacResultado(DisponibilidadRbac.NoDisponible, new EjesRbac(false, false), null, "prueba"), null));

        public Task<IReadOnlyList<HallazgoResueltoFila>> LeerHallazgosResueltosAsync(
            int clientId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RegistroBarrido> LeerBarridoResueltoAsync(int clientId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>Ningún test de esta clase pega a /preview/variacion-consumo: revienta a propósito si
    /// algo llega a llamarlo, mismo criterio que FakeInsumosBdRecolectorVacio.</summary>
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
