using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

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
        public Task<int> ReplaceFacturacionAsync(
            int clientId, string fileName, string? user, ParseResult<FacturacionRow> parsed, CancellationToken ct)
            => Task.FromResult(1);

        public Task<int> ReplaceCasosAsync(
            int clientId, string fileName, string? user, ParseResult<CasoRow> parsed, CancellationToken ct)
            => Task.FromResult(1);

        public Task DeleteInsumoAsync(int clientId, string kind, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<InsumoEstado>>([]);
    }

    /// <summary>Ensamblador falso que revienta si se lo llega a usar: ver el comentario de
    /// ConfigureWebHost sobre por qué está acá aunque nada de esta clase lo necesite.</summary>
    public sealed class FakeInsumosBdRecolectorVacio : IInsumosBdRecolector
    {
        public Task<InsumosBd> LeerAsync(int clientId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
