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
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// GET /informe-valor/clients/{clientId}/insumos-bd: comportamiento del endpoint de diagnóstico
/// que expone el ensamblador (<see cref="IInsumosBdRecolector"/>). Mismo patrón que
/// InformeValorUploadApiTests (pipeline MVC real: auth, roles, RequireModule, IAnalysisAccess,
/// solo BD fake), con un falso del ensamblador en vez de un falso del store de insumos subidos.
/// </summary>
public sealed class InsumosBdRecolectorTests : IClassFixture<InsumosBdRecolectorTests.Factory>
{
    private readonly Factory _factory;
    public InsumosBdRecolectorTests(Factory factory) => _factory = factory;

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

    [Fact]
    public async Task Sin_acceso_al_cliente_devuelve_403()
    {
        _factory.Access.Deny(clientId: 99);
        var client = ClientFor("c1@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.GetAsync("/informe-valor/clients/99/insumos-bd");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Con_acceso_devuelve_conteos_y_el_estado_de_rbac()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("c2@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.GetAsync("/informe-valor/clients/7/insumos-bd");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("advisor", out _));
        Assert.True(body.TryGetProperty("estado_rbac", out _));
    }

    /// <summary>
    /// Cobertura de forma además del contenido: el consultor necesita las tres cuentas de Advisor,
    /// las dos de la matriz (total + excluidos), la de RBAC y la de retiros para decidir si el
    /// cliente tiene datos suficientes antes de generar el informe.
    /// </summary>
    [Fact]
    public async Task Con_acceso_devuelve_todos_los_bloques_de_conteo()
    {
        _factory.Access.Allow(clientId: 7);
        _factory.Recolector.ConDatosDePrueba();
        var client = ClientFor("c2b@bit.ec", Roles.Consultor, canEdit: false);

        var body = await client.GetFromJsonAsync<JsonElement>("/informe-valor/clients/7/insumos-bd");

        var advisor = body.GetProperty("advisor");
        Assert.Equal(1, advisor.GetProperty("total").GetInt32());
        Assert.Equal(1, advisor.GetProperty("suscripciones").GetInt32());
        Assert.Equal(1, advisor.GetProperty("con_ahorro").GetInt32());

        // 3 filas de matriz, 1 excluida (split 2-1 a proposito): si el predicado de produccion se
        // invirtiera (Count(m => m.Excluida) -> Count(m => !m.Excluida)) esto daria excluidos=2 en
        // vez de 1 y el assert lo detecta. Con un split 1-1 los dos conteos coinciden en 1 y la
        // mutacion pasa sin que nada la note (probado a mano, descartado por eso).
        var matriz = body.GetProperty("matriz");
        Assert.Equal(3, matriz.GetProperty("total").GetInt32());
        Assert.Equal(1, matriz.GetProperty("excluidos").GetInt32());

        Assert.Equal(0, body.GetProperty("rbac").GetProperty("asignaciones").GetInt32());
        Assert.Equal(0, body.GetProperty("retiros").GetProperty("total").GetInt32());

        var estadoRbac = body.GetProperty("estado_rbac");
        Assert.Equal("completo", estadoRbac.GetProperty("disponibilidad").GetString());
        Assert.True(estadoRbac.GetProperty("estado_cuenta_medido").GetBoolean());
        Assert.True(estadoRbac.GetProperty("ultimo_login_medido").GetBoolean());
    }

    /// <summary>
    /// Decisión documentada en SqlInsumosBdRecolector/InformeValorController: "excluidos" es un
    /// conteo SOLO de la matriz (is_excluded vive en la canónica y es el flag que el consultor cura
    /// desde la pantalla de la matriz). El bloque de Advisor no lo repite, ni siquiera en cero, para
    /// que nadie confunda "no reportado" con "no hay excluidos en Advisor".
    /// </summary>
    [Fact]
    public async Task El_bloque_de_advisor_no_reporta_excluidos()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("c2c@bit.ec", Roles.Consultor, canEdit: false);

        var body = await client.GetFromJsonAsync<JsonElement>("/informe-valor/clients/7/insumos-bd");

        Assert.False(body.GetProperty("advisor").TryGetProperty("excluidos", out _));
        Assert.True(body.GetProperty("matriz").TryGetProperty("excluidos", out _));
    }

    /// <summary>El endpoint es de conteos: no puede filtrar nombres de recurso ni de identidad.</summary>
    [Fact]
    public async Task No_expone_nombres_de_recurso_ni_de_identidad()
    {
        _factory.Access.Allow(clientId: 7);
        _factory.Recolector.ConDatosDePrueba(); // el falso siembra un nombre reconocible
        var client = ClientFor("c3@bit.ec", Roles.Consultor, canEdit: false);

        var texto = await (await client.GetAsync("/informe-valor/clients/7/insumos-bd")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("vm-secreta", texto, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Fixture: API real en memoria, solo se fake-an auth/acceso/permisos y el ensamblador ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeAnalysisAccessForInsumosBd Access { get; } = new();
        public FakeInsumosBdRecolector Recolector { get; } = new();
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
                // El controller también pide IInformeValorStore (constructor compartido con
                // Subir/Estado/Borrar): ningún test de esta clase lo ejercita, pero un falso que
                // revienta si se llega a usar deja la fixture "solo BD fake" de verdad, en vez de
                // depender en silencio de que SqlInformeValorStore no abra conexión al construirse.
                services.RemoveAll<IInformeValorStore>();
                services.AddSingleton<IInformeValorStore>(new FakeInformeValorStoreVacio());
                services.RemoveAll<IInsumosBdRecolector>();
                services.AddSingleton<IInsumosBdRecolector>(Recolector);
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
    /// vacío (deny por defecto, igual que un consultor sin asignaciones en dbo.user_client_assignment).
    /// Mismo comportamiento que InformeValorUploadApiTests.FakeAnalysisAccessForInformeValor, copiado
    /// acá para que este archivo no dependa de las clases anidadas de otro archivo de test.</summary>
    public sealed class FakeAnalysisAccessForInsumosBd : IAnalysisAccess
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

    /// <summary>Store de insumos subidos: ningún test de esta clase lo llega a invocar (todos van a
    /// /insumos-bd, no a /insumos/{kind}), así que revienta a propósito si algo lo llama.</summary>
    public sealed class FakeInformeValorStoreVacio : IInformeValorStore
    {
        public Task<int> ReplaceFacturacionAsync(
            int clientId, string fileName, string? user, ParseResult<FacturacionRow> parsed, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> ReplaceCasosAsync(
            int clientId, string fileName, string? user, ParseResult<CasoRow> parsed, CancellationToken ct)
            => throw new NotSupportedException();

        public Task DeleteInsumoAsync(int clientId, string kind, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>Ensamblador falso: por defecto devuelve todo vacío (RBAC no disponible, sin datos);
    /// <see cref="ConDatosDePrueba"/> siembra una fila de cada fuente con un nombre de recurso
    /// reconocible ("vm-secreta"), para que el test de fuga de datos tenga algo concreto que
    /// buscar en el texto de la respuesta.</summary>
    public sealed class FakeInsumosBdRecolector : IInsumosBdRecolector
    {
        private InsumosBd _insumos = Vacio();

        private static InsumosBd Vacio() => new(
            Advisor: [],
            Matriz: [],
            Rbac: [],
            Retiros: [],
            EstadoRbac: new EstadoRbacResultado(
                DisponibilidadRbac.NoDisponible, new EjesRbac(false, false), null,
                "Sin datos de prueba."),
            SeguridadGestionadaExternamente: false,
            SeguridadGestionadaNota: null,
            LeidoEn: DateTime.UtcNow);

        public void ConDatosDePrueba()
        {
            _insumos = new InsumosBd(
                Advisor:
                [
                    new AdvisorFila(
                        PillarNumber: 1, Pilar: "Confiabilidad", ImpactNumber: 1, Impacto: "Alto",
                        Recomendacion: "Recomendación de prueba", RecomendacionEn: null,
                        CanonicalId: 1, MatrixCode: null, Source: null,
                        SubscriptionId: "sub-1", SubscriptionName: "Suscripción de prueba",
                        ResourceName: "vm-secreta", ResourceType: "Microsoft.Compute/virtualMachines",
                        AhorroAnual: 100m, MonedaAhorro: "USD"),
                ],
                // Tres filas, dos sin excluir y una excluida (split A PROPOSITO desigual): si el
                // conteo de "excluidos" invirtiera el predicado (Count(m => m.Excluida) ->
                // Count(m => !m.Excluida)) daria 2 en vez de 1. Con un split 1-1 (probado y
                // descartado) los dos conteos coinciden en 1 y la mutacion pasa sin que nada la
                // note; con 2-1 el resultado cambia y el test SI la detecta (verificado a mano
                // invirtiendo el predicado en el controller: con este seed el test falla).
                Matriz:
                [
                    new MatrizFila(
                        CanonicalId: 1, MatrixCode: null, PillarNumber: 1, Ambito: "Confiabilidad",
                        Hallazgo: "vm-secreta necesita revisión", Fecha: null, ImpactNumber: 1,
                        Prioridad: "1", EsfuerzoTexto: "2-3 días", AvancePct: 0, Registro: null,
                        ResourceCount: 1, Excluida: false),
                    new MatrizFila(
                        CanonicalId: 2, MatrixCode: null, PillarNumber: 2, Ambito: "Excelencia operativa",
                        Hallazgo: "Segundo hallazgo sin excluir", Fecha: null, ImpactNumber: 2,
                        Prioridad: "2", EsfuerzoTexto: "medio día", AvancePct: 50, Registro: null,
                        ResourceCount: 1, Excluida: false),
                    new MatrizFila(
                        CanonicalId: 3, MatrixCode: null, PillarNumber: 3, Ambito: "Seguridad",
                        Hallazgo: "Hallazgo excluido por el consultor", Fecha: null, ImpactNumber: 3,
                        Prioridad: "3", EsfuerzoTexto: "1 día", AvancePct: 100, Registro: null,
                        ResourceCount: 1, Excluida: true),
                ],
                Rbac: [],
                Retiros: [],
                EstadoRbac: new EstadoRbacResultado(
                    DisponibilidadRbac.Completo, new EjesRbac(true, true), DateTime.UtcNow,
                    "Completo, con datos de prueba."),
                SeguridadGestionadaExternamente: false,
                SeguridadGestionadaNota: null,
                LeidoEn: DateTime.UtcNow);
        }

        public Task<InsumosBd> LeerAsync(int clientId, CancellationToken ct = default) => Task.FromResult(_insumos);
    }
}
