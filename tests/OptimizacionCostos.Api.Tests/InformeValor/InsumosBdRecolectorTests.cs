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

    /// <summary>
    /// Mismo criterio que El_bloque_de_advisor_no_reporta_excluidos: un cero no puede leerse como
    /// dos cosas distintas. Sin este bloque, un pilar de Seguridad en cero en advisor/matriz se ve
    /// igual para "el cliente no tiene hallazgos" y para "el cliente gestiona su seguridad aparte y
    /// MatrizRecolector/AdvisorRecolector ya la excluyeron" — quien depure de donde salio la cifra
    /// va a sospechar de la sincronizacion en vez de leer la decision real del cliente.
    /// </summary>
    [Fact]
    public async Task El_endpoint_expone_la_bandera_y_la_nota_de_seguridad_gestionada()
    {
        _factory.Access.Allow(clientId: 7);
        _factory.Recolector.ConSeguridadGestionadaExternamente("Controles revisados por el CSIRT del cliente.");
        var client = ClientFor("c4@bit.ec", Roles.Consultor, canEdit: false);

        var body = await client.GetFromJsonAsync<JsonElement>("/informe-valor/clients/7/insumos-bd");

        var seguridad = body.GetProperty("seguridad_gestionada");
        Assert.True(seguridad.GetProperty("gestionada_externamente").GetBoolean());
        Assert.Equal("Controles revisados por el CSIRT del cliente.", seguridad.GetProperty("nota").GetString());
    }

    /// <summary>Complemento del test anterior: sin gestion externa, la bandera es false y la nota
    /// es null (no el texto por defecto) -- DefaultIgnoreCondition.Never en Program.cs asegura que
    /// "nota" sigue presente en el JSON aunque valga null, en vez de desaparecer.</summary>
    [Fact]
    public async Task Sin_gestion_externa_la_bandera_es_false_y_la_nota_es_null()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("c4b@bit.ec", Roles.Consultor, canEdit: false);

        var body = await client.GetFromJsonAsync<JsonElement>("/informe-valor/clients/7/insumos-bd");

        var seguridad = body.GetProperty("seguridad_gestionada");
        Assert.False(seguridad.GetProperty("gestionada_externamente").GetBoolean());
        Assert.Equal(JsonValueKind.Null, seguridad.GetProperty("nota").ValueKind);
    }

    /// <summary>
    /// El cable de RBAC (Tarea 1): quien depure de dónde salió una cifra del bloque de seguridad
    /// necesita saber si vino de la base o del archivo, no solo si la base estaba completa -- los
    /// dos pueden discrepar (base parcial, insumo efectivo el archivo).
    /// </summary>
    [Fact]
    public async Task El_endpoint_expone_el_origen_del_rbac()
    {
        _factory.Access.Allow(clientId: 7);
        _factory.Recolector.ConOrigenRbac(InsumosBd.OrigenArchivo);
        var client = ClientFor("c5@bit.ec", Roles.Consultor, canEdit: false);

        var body = await client.GetFromJsonAsync<JsonElement>("/informe-valor/clients/7/insumos-bd");

        Assert.Equal(InsumosBd.OrigenArchivo, body.GetProperty("estado_rbac").GetProperty("origen").GetString());
    }

    /// <summary>Complemento: sin ninguna de las dos fuentes, el origen es null (JSON real), no una
    /// cadena vacía ni el string "null".</summary>
    [Fact]
    public async Task Sin_ninguna_fuente_el_origen_es_null()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("c5b@bit.ec", Roles.Consultor, canEdit: false);

        var body = await client.GetFromJsonAsync<JsonElement>("/informe-valor/clients/7/insumos-bd");

        Assert.Equal(JsonValueKind.Null, body.GetProperty("estado_rbac").GetProperty("origen").ValueKind);
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

    // ---- GET /estado: estado_rbac tiene que salir del camino liviano, con la misma forma que
    // /insumos-bd (la pantalla de insumos dejó de pagar /insumos-bd completo solo para leer esta
    // condicional al cargar y después de cada subida o borrado) ----

    [Fact]
    public async Task Estado_sin_acceso_al_cliente_devuelve_403()
    {
        _factory.Access.Deny(clientId: 99);
        var client = ClientFor("e1@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.GetAsync("/informe-valor/clients/99/estado");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Estado_con_acceso_devuelve_insumos_y_estado_rbac()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("e2@bit.ec", Roles.Consultor, canEdit: false);

        var body = await client.GetFromJsonAsync<JsonElement>("/informe-valor/clients/7/estado");

        Assert.True(body.TryGetProperty("insumos", out _));
        Assert.True(body.TryGetProperty("estado_rbac", out _));
    }

    /// <summary>
    /// El requisito central de la tarea: /estado tiene que devolver estado_rbac con exactamente
    /// los mismos campos y valores que /insumos-bd para el mismo cliente en el mismo instante --
    /// el front consume un solo tipo para los dos. Comparar los NOMBRES de propiedad (no solo los
    /// valores) detecta tanto un campo de más/menos como el defecto que ya causó confusión una vez
    /// (rbac_origen en vez de origen).
    /// </summary>
    [Fact]
    public async Task Estado_expone_el_mismo_bloque_estado_rbac_que_insumos_bd()
    {
        _factory.Access.Allow(clientId: 7);
        _factory.Recolector.ConDatosDePrueba();
        var client = ClientFor("e3@bit.ec", Roles.Consultor, canEdit: false);

        var deEstado = (await client.GetFromJsonAsync<JsonElement>("/informe-valor/clients/7/estado"))
            .GetProperty("estado_rbac");
        var deInsumosBd = (await client.GetFromJsonAsync<JsonElement>("/informe-valor/clients/7/insumos-bd"))
            .GetProperty("estado_rbac");

        var nombresEstado = deEstado.EnumerateObject().Select(p => p.Name).OrderBy(n => n);
        var nombresInsumosBd = deInsumosBd.EnumerateObject().Select(p => p.Name).OrderBy(n => n);
        Assert.Equal(nombresInsumosBd, nombresEstado);

        Assert.Equal(
            deInsumosBd.GetProperty("disponibilidad").GetString(),
            deEstado.GetProperty("disponibilidad").GetString());
        Assert.Equal(
            deInsumosBd.GetProperty("estado_cuenta_medido").GetBoolean(),
            deEstado.GetProperty("estado_cuenta_medido").GetBoolean());
        Assert.Equal(
            deInsumosBd.GetProperty("ultimo_login_medido").GetBoolean(),
            deEstado.GetProperty("ultimo_login_medido").GetBoolean());
        Assert.Equal(deInsumosBd.GetProperty("motivo").GetString(), deEstado.GetProperty("motivo").GetString());
        Assert.Equal(NullableString(deInsumosBd, "fecha_corrida"), NullableString(deEstado, "fecha_corrida"));
        Assert.Equal(NullableString(deInsumosBd, "origen"), NullableString(deEstado, "origen"));
    }

    /// <summary>Ojo con el detalle que ya causó confusión una vez: el campo es "origen", nunca
    /// "rbac_origen".</summary>
    [Fact]
    public async Task Estado_expone_el_origen_del_rbac_como_origen_no_rbac_origen()
    {
        _factory.Access.Allow(clientId: 7);
        _factory.Recolector.ConOrigenRbac(InsumosBd.OrigenArchivo);
        var client = ClientFor("e4@bit.ec", Roles.Consultor, canEdit: false);

        var estadoRbac = (await client.GetFromJsonAsync<JsonElement>("/informe-valor/clients/7/estado"))
            .GetProperty("estado_rbac");

        Assert.Equal(InsumosBd.OrigenArchivo, estadoRbac.GetProperty("origen").GetString());
        Assert.False(estadoRbac.TryGetProperty("rbac_origen", out _));
    }

    /// <summary>
    /// El motivo de toda la tarea: la pantalla de insumos no puede volver a pagar Advisor/Matriz/
    /// Retiros (el recolector completo, <see cref="FakeInsumosBdRecolector.LeerAsync"/>) solo para
    /// leer esta condicional. Compara el conteo de llamadas antes/después en vez de un booleano
    /// "prohibido": la Factory es un solo fixture compartido por toda la clase, así que un booleano
    /// que un test deja en true contaminaría a cualquier test posterior sin relación con este.
    /// </summary>
    [Fact]
    public async Task Estado_no_paga_el_recolector_completo()
    {
        _factory.Access.Allow(clientId: 7);
        _factory.Recolector.ConDatosDePrueba();
        var client = ClientFor("e5@bit.ec", Roles.Consultor, canEdit: false);
        var llamadasAntes = _factory.Recolector.LeerAsyncLlamadas;

        var res = await client.GetAsync("/informe-valor/clients/7/estado");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(llamadasAntes, _factory.Recolector.LeerAsyncLlamadas);
    }

    /// <summary>Null en JSON real, no la cadena "null" ni una propiedad ausente -- mismo criterio
    /// que Sin_ninguna_fuente_el_origen_es_null.</summary>
    private static string? NullableString(JsonElement obj, string prop) =>
        obj.GetProperty(prop).ValueKind == JsonValueKind.Null ? null : obj.GetProperty(prop).GetString();

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
                // Entrega 2d: el controller también pide IReservationService/IAzureReservationsClient
                // (la foto de reservas de /preview/variacion-consumo). Ningún test de esta clase pega
                // a esa ruta (todos van a /estado o /insumos-bd), pero sin este reemplazo el
                // controller se construiria con las implementaciones reales, mismo motivo que
                // FakeInformeValorStoreVacio de abajo.
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

    /// <summary>Store de insumos subidos: las mutaciones (Replace*/Borrar) revientan a propósito
    /// (ningún test de esta clase sube ni borra, todos van a /estado o /insumos-bd). GetEstadoAsync
    /// sí responde -- vacío, no reventado -- porque GET /estado lo necesita para el bloque
    /// "insumos"; ningún test de esta clase mira ese bloque en detalle, así que una lista vacía
    /// (nada cargado) alcanza.</summary>
    public sealed class FakeInformeValorStoreVacio : IInformeValorStore
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

        public Task<int> ReplaceEvolucionAsync(
            int clientId, string fileName, string? user, ParseResult<EvolucionRow> parsed, CancellationToken ct)
            => throw new NotSupportedException();

        public Task DeleteInsumoAsync(int clientId, string kind, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<InsumoEstado>>([]);

        public Task<IReadOnlyList<FacturacionRow>> GetFacturacionAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CasoRow>> GetCasosAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RbacFila>> GetRbacAsync(int clientId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<EvolucionRow>> GetEvolucionAsync(int clientId, CancellationToken ct)
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

    /// <summary>Ensamblador falso: por defecto devuelve todo vacío (RBAC no disponible, sin datos);
    /// <see cref="ConDatosDePrueba"/> siembra una fila de cada fuente con un nombre de recurso
    /// reconocible ("vm-secreta"), para que el test de fuga de datos tenga algo concreto que
    /// buscar en el texto de la respuesta.</summary>
    public sealed class FakeInsumosBdRecolector : IInsumosBdRecolector
    {
        private InsumosBd _insumos = Vacio();

        /// <summary>Cuántas veces se llamó <see cref="LeerAsync"/> (el recolector completo) en
        /// total. Un contador que solo crece, nunca un booleano "prohibido" que reviente: la
        /// Factory es <see cref="Xunit.IClassFixture{TFixture}"/> (una sola instancia para TODA la
        /// clase de test), así que un booleano compartido que un test deja en true contaminaría a
        /// cualquier otro test posterior que sí necesite <see cref="LeerAsync"/> -- el propio test
        /// de esta guarda lo descubrió: comparar el conteo antes/después de una sola llamada es
        /// insensible al orden en que xUnit corra el resto de la clase.</summary>
        public int LeerAsyncLlamadas { get; private set; }

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
                        ResourceGroup: "rg-prueba",
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

        /// <summary>Marca el cliente como si gestionara su seguridad por fuera, sobre el estado
        /// actual (Vacio() o ConDatosDePrueba(), lo que se haya llamado antes): solo toca los dos
        /// campos nuevos, para no acoplar este escenario a los otros datos de prueba.</summary>
        public void ConSeguridadGestionadaExternamente(string? nota) =>
            _insumos = _insumos with { SeguridadGestionadaExternamente = true, SeguridadGestionadaNota = nota };

        /// <summary>Mismo patrón que ConSeguridadGestionadaExternamente: solo toca RbacOrigen,
        /// sobre el estado actual.</summary>
        public void ConOrigenRbac(string? origen) => _insumos = _insumos with { RbacOrigen = origen };

        public Task<InsumosBd> LeerAsync(int clientId, CancellationToken ct = default)
        {
            LeerAsyncLlamadas++;
            return Task.FromResult(_insumos);
        }

        public Task<EstadoRbacResultado> LeerEstadoRbacAsync(int clientId, CancellationToken ct = default) =>
            Task.FromResult(_insumos.EstadoRbac);

        /// <summary>Mismo par (EstadoRbac, RbacOrigen) que ve /insumos-bd, leído del mismo
        /// <see cref="_insumos"/> de este falso -- así los tests de paridad entre /estado e
        /// /insumos-bd (Estado_expone_el_mismo_bloque_estado_rbac_que_insumos_bd) comparan contra
        /// una única fuente de verdad, en vez de mantener dos siembras que podrían desincronizarse.</summary>
        public Task<(EstadoRbacResultado Estado, string? Origen)> LeerEstadoRbacConOrigenAsync(
            int clientId, CancellationToken ct = default) =>
            Task.FromResult((_insumos.EstadoRbac, _insumos.RbacOrigen));

        /// <summary>Mismo <see cref="_insumos"/> que el camino completo, para que los dos caminos no
        /// puedan devolver universos distintos en un test. Ningún test de esta clase pega a
        /// /preview/variacion-consumo, que es su único consumidor.</summary>
        public Task<IReadOnlyList<HallazgoResueltoFila>> LeerHallazgosResueltosAsync(
            int clientId, CancellationToken ct = default) =>
            Task.FromResult(_insumos.HallazgosResueltos ?? []);
    }

    /// <summary>Ningún test de esta clase pega a /preview/variacion-consumo: revienta a propósito si
    /// algo llega a llamarlo, mismo criterio que FakeInformeValorStoreVacio.</summary>
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
