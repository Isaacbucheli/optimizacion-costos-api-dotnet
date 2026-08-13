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
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;
using OptimizacionCostos.Api.Features.InformeValor.Entrega;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// Las dos fases de la vista previa: POST /informe-valor/clients/{clientId}/preview (Tarea 8 de la
/// entrega 2b) y POST .../preview/variacion-consumo (entrega 2d), que es la que lee las reservas del
/// cliente en vivo contra Azure. Mismo patrón que InformeValorUploadApiTests/InsumosBdRecolectorTests:
/// pipeline MVC real (auth, roles, RequireModule, IAnalysisAccess), solo BD fake.
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

    /// <summary>
    /// Por esto existe /preview/variacion-consumo: <c>/preview</c> NO lee reservas, ni siquiera
    /// cuando el cliente tiene credencial y reservas de verdad para leer. Esa lectura cuesta una
    /// llamada a Consumption por reserva activa, en secuencia, y es lo que hacía lento el primer
    /// render de la pantalla. El eje viaja "no medido" y el consumidor lo completa con la segunda
    /// llamada.
    ///
    /// <para>La aserción que importa es el contador: sin él, un /preview que volviera a capturar la
    /// foto seguiría pasando el resto de este archivo sin que nadie lo note.</para>
    /// </summary>
    [Fact]
    public async Task El_preview_no_lee_reservas_aunque_el_cliente_tenga()
    {
        const int clientId = 68;
        _factory.Access.Allow(clientId);
        SembrarUnaReservaConConsumidor(clientId, credentialId: 2, reservationId: "resv-68");
        var client = ClientFor("p8@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.PostAsJsonAsync($"/informe-valor/clients/{clientId}/preview", CuerpoValido());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(0, _factory.Reservations.LlamadasACredenciales(clientId));

        var reservas = (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("fact").GetProperty("variacionConsumo").GetProperty("reservas");
        Assert.False(reservas.GetProperty("medido").GetBoolean());
        // El motivo tiene que decir que el dato se pide aparte: este cero y el de un cliente sin
        // ninguna reserva se ven idénticos, y son dos cosas distintas.
        Assert.Contains("aparte", reservas.GetProperty("motivo").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sin_acceso_al_cliente_la_variacion_de_consumo_devuelve_403()
    {
        _factory.Access.Deny(clientId: 98);
        var client = ClientFor("p9@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.PostAsJsonAsync(
            "/informe-valor/clients/98/preview/variacion-consumo", CuerpoValido());

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        // El guard va antes de tocar Azure: un cliente ajeno no dispara ninguna lectura de reservas.
        Assert.Equal(0, _factory.Reservations.LlamadasACredenciales(98));
    }

    /// <summary>Las dos fases rechazan el mismo cuerpo: si una aceptara un rango invertido que la
    /// otra rechaza, el bloque que vuelve mediría una ventana que el informe no reconoce.</summary>
    [Fact]
    public async Task Con_periodo_invertido_la_variacion_de_consumo_devuelve_400()
    {
        _factory.Access.Allow(clientId: 7);
        var client = ClientFor("p10@bit.ec", Roles.Consultor, canEdit: false);

        var cuerpo = new
        {
            period_start = "2026-02-28", period_end = "2026-01-01",
            corte = "2026-03-01T00:00:00Z", meses_parciales_forzados = Array.Empty<string>(),
        };
        var res = await client.PostAsJsonAsync("/informe-valor/clients/7/preview/variacion-consumo", cuerpo);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    /// <summary>
    /// Una falla leyendo credenciales -- el hueco que <c>ReservasRecolector.CapturarAsync</c> no
    /// atrapa (<c>IReservationService.ActiveCredentialsAsync</c> es una consulta SQL sin try/catch
    /// alrededor, ver el comentario de clase de <c>InformeValorController.CapturarFotoReservasAsync</c>)
    /// -- no puede tumbar la respuesta. El eje de reservas sale "no medido" y los otros dos baldes,
    /// que no dependen de reservas, se calculan igual.
    /// </summary>
    [Fact]
    public async Task Si_la_lectura_de_reservas_revienta_la_variacion_sale_igual_con_el_eje_no_medido()
    {
        _factory.Access.Allow(clientId: 66);
        _factory.Reservations.FallarLecturaDeCredenciales(66);
        var client = ClientFor("p6@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.PostAsJsonAsync(
            "/informe-valor/clients/66/preview/variacion-consumo", CuerpoValido());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var reservas = json.GetProperty("reservas");
        Assert.False(reservas.GetProperty("medido").GetBoolean());
        Assert.NotEmpty(reservas.GetProperty("motivo").GetString()!);
        // El bloque completo sigue viajando: con dos meses de facturación no alcanza para la ventana
        // fija, así que la atribución es null -- pero es el mismo null que devolvería /preview, no un
        // error.
        Assert.Equal(JsonValueKind.Null, json.GetProperty("atribucion").ValueKind);
    }

    /// <summary>
    /// El camino feliz de la fase 2: con una credencial activa y una reserva con un consumidor
    /// confirmado, la foto llega MEDIDA hasta el JSON. Es lo que <c>/preview</c> no hace y por lo que
    /// esta segunda llamada existe.
    /// </summary>
    [Fact]
    public async Task Con_una_reserva_activa_y_un_consumidor_confirmado_el_eje_llega_medido()
    {
        const int clientId = 67;
        _factory.Access.Allow(clientId);
        SembrarUnaReservaConConsumidor(clientId, credentialId: 1, reservationId: "resv-1");
        var client = ClientFor("p7@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.PostAsJsonAsync(
            $"/informe-valor/clients/{clientId}/preview/variacion-consumo", CuerpoValido());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var reservas = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reservas");
        Assert.True(reservas.GetProperty("medido").GetBoolean());
        var confirmados = reservas.GetProperty("confirmados");
        Assert.Equal(1, confirmados.GetArrayLength());
        // camelCase, igual que el bloque hermano `atribucion`: el nombre lo declara el modelo, no lo
        // infiere la política de serialización (ver ModeloInformeValorSerializacionTests).
        Assert.Equal("vm-1", confirmados[0].GetProperty("resourceName").GetString());
        Assert.Equal(0, reservas.GetProperty("reservasConConsumidoresNoLeidos").GetInt32());
    }

    /// <summary>
    /// El caso que se veía igual que un cliente sin ahorro confirmado: la credencial lista reservas
    /// (Microsoft.Capacity) pero no puede leer consumidores (Microsoft.Consumption). El eje sigue
    /// medido —la lista se leyó— pero la respuesta tiene que decir que la cifra está incompleta, en
    /// el conteo, en la fila de la reserva y en el motivo. Sin eso, "cero confirmados" significa dos
    /// cosas opuestas con el mismo JSON.
    /// </summary>
    [Fact]
    public async Task Si_los_consumidores_no_se_pudieron_leer_la_respuesta_lo_dice_en_vez_de_publicar_cero()
    {
        const int clientId = 69;
        _factory.Access.Allow(clientId);
        SembrarUnaReservaConConsumidor(clientId, credentialId: 3, reservationId: "resv-69");
        _factory.ReservationsClient.ConFallaDeConsumidores("resv-69");
        var client = ClientFor("p11@bit.ec", Roles.Consultor, canEdit: false);

        var res = await client.PostAsJsonAsync(
            $"/informe-valor/clients/{clientId}/preview/variacion-consumo", CuerpoValido());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var reservas = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reservas");
        Assert.True(reservas.GetProperty("medido").GetBoolean());
        Assert.Empty(reservas.GetProperty("confirmados").EnumerateArray());
        Assert.Equal(0m, reservas.GetProperty("ahorroConfirmado").GetDecimal());

        Assert.Equal(1, reservas.GetProperty("reservasConConsumidoresNoLeidos").GetInt32());
        var estimado = Assert.Single(reservas.GetProperty("estimados").EnumerateArray().ToList());
        Assert.True(estimado.GetProperty("consumidoresNoLeidos").GetBoolean());
        Assert.DoesNotContain("completas", reservas.GetProperty("motivo").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// La fase 2 no vuelve a pagar el recolector completo (Advisor, Matriz, Retiros, la corrida de
    /// Revisión de accesos con su snapshot y el Excel de RBAC): de todo eso, el bloque de variación
    /// del consumo usa un solo campo, los hallazgos resueltos. La fase 1 ya pagó el resto hace un
    /// segundo, en la misma vista previa.
    /// </summary>
    [Fact]
    public async Task La_variacion_de_consumo_no_vuelve_a_pagar_el_recolector_completo()
    {
        _factory.Access.Allow(clientId: 70);
        var client = ClientFor("p12@bit.ec", Roles.Consultor, canEdit: false);
        var completoAntes = _factory.Recolector.LeerAsyncLlamadas;
        var angostoAntes = _factory.Recolector.LeerHallazgosResueltosLlamadas;

        var res = await client.PostAsJsonAsync(
            "/informe-valor/clients/70/preview/variacion-consumo", CuerpoValido());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(completoAntes, _factory.Recolector.LeerAsyncLlamadas);
        Assert.Equal(angostoAntes + 1, _factory.Recolector.LeerHallazgosResueltosLlamadas);
    }

    /// <summary>El contraste del test de arriba: la fase 1 sí necesita el recolector completo (los
    /// otros cuatro bloques del informe salen de ahí), así que el camino angosto no lo reemplaza,
    /// lo complementa.</summary>
    [Fact]
    public async Task El_preview_si_paga_el_recolector_completo()
    {
        _factory.Access.Allow(clientId: 71);
        var client = ClientFor("p13@bit.ec", Roles.Consultor, canEdit: false);
        var antes = _factory.Recolector.LeerAsyncLlamadas;

        var res = await client.PostAsJsonAsync("/informe-valor/clients/71/preview", CuerpoValido());

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(antes + 1, _factory.Recolector.LeerAsyncLlamadas);
    }

    /// <summary>Una credencial activa, una reserva viva y un consumidor confirmado sobre vm-1, que es
    /// el recurso que factura en <see cref="FakeInformeValorStoreConDatos"/>. Datos sintéticos:
    /// ningún nombre de cliente ni de recurso real (regla dura del repo).</summary>
    private void SembrarUnaReservaConConsumidor(int clientId, int credentialId, string reservationId)
    {
        _factory.Reservations.ConCredencialYReservas(clientId, new CredentialRef(credentialId, "cred-prueba"),
        [
            new ReservationDto(
                ReservationId: reservationId, CredentialId: credentialId, Name: "Reserva de prueba",
                Product: "Standard_D2s_v5", Region: "eastus", Quantity: 2, Term: "P1Y", TermLabel: "1 ano",
                State: "Succeeded", AppliedScopeType: null, AppliedScopes: [], ExpiresOn: "2027-06-01",
                DaysRemaining: 300, Expired: false, Expiring: false, UtilizationLast: "80%",
                Utilization7d: "75%"),
        ]);
        _factory.ReservationsClient.ConConsumidores(reservationId,
        [
            new ReservationConsumer(
                InstanceId: "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines/vm-1",
                ResourceName: "vm-1", ResourceGroup: "rg-1", SubscriptionId: "sub-1", SubscriptionName: null,
                SkuName: "Standard_D2s_v5", UsedHours: 700, LastSeen: "2026-02-20", DaysSeen: 28),
        ]);
    }

    // ---- Fixture: API real en memoria, solo se fake-an auth/acceso/permisos y las cuatro fuentes ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeAnalysisAccessForPreview Access { get; } = new();
        public FakeInformeValorStoreConDatos Store { get; } = new();
        public FakeInsumosBdRecolectorVacio Recolector { get; } = new();
        public FakeClientStore ClientStore { get; } = new();
        public FakeReservationServiceControlable Reservations { get; } = new();
        public FakeAzureReservationsClientControlable ReservationsClient { get; } = new();
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
                // Entrega 2d: la foto de reservas de /preview/variacion-consumo. Por defecto (ningún
                // test la configura) responde "sin credenciales activas" para cualquier client_id, el
                // mismo estado seguro que ReservasRecolector.CapturarAsync da sin filas en
                // client_azure_credentials -- así los tests que solo pegan a /preview no pagan
                // ninguna llamada real a Azure ni a SQL.
                services.RemoveAll<IReservationService>();
                services.AddSingleton<IReservationService>(Reservations);
                services.RemoveAll<IAzureReservationsClient>();
                services.AddSingleton<IAzureReservationsClient>(ReservationsClient);
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

        public Task<int> ReplaceEvolucionAsync(
            int clientId, string fileName, string? user, ParseResult<EvolucionRow> parsed, CancellationToken ct)
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

    public sealed class FakeInsumosBdRecolectorVacio : IInsumosBdRecolector
    {
        /// <summary>Cuántas veces se pidió el recolector COMPLETO (Advisor, Matriz, Retiros, la
        /// corrida de accesos con su snapshot y, si hace falta, el Excel de RBAC). Contadores por
        /// método y no un booleano: la Factory es <see cref="IClassFixture{TFixture}"/>, o sea una
        /// sola instancia para toda la clase, así que cada test compara el delta de su propia
        /// llamada -- mismo criterio que el contador de InsumosBdRecolectorTests.</summary>
        public int LeerAsyncLlamadas { get; private set; }

        /// <summary>Ídem para el camino angosto del balde 2.</summary>
        public int LeerHallazgosResueltosLlamadas { get; private set; }

        public Task<InsumosBd> LeerAsync(int clientId, CancellationToken ct = default)
        {
            LeerAsyncLlamadas++;
            return Task.FromResult(new InsumosBd(
                Advisor: [], Matriz: [], Rbac: [], Retiros: [],
                EstadoRbac: new EstadoRbacResultado(
                    DisponibilidadRbac.NoDisponible, new EjesRbac(false, false), null, "Sin datos de prueba."),
                SeguridadGestionadaExternamente: false, SeguridadGestionadaNota: null,
                LeidoEn: new DateTime(2026, 1, 1)));
        }

        public Task<EstadoRbacResultado> LeerEstadoRbacAsync(int clientId, CancellationToken ct = default) =>
            Task.FromResult(new EstadoRbacResultado(
                DisponibilidadRbac.NoDisponible, new EjesRbac(false, false), null, "Sin datos de prueba."));

        // Ningún test de este archivo pega a /estado (todos van a /preview o a su fase 2): revienta
        // a propósito si algo llega a llamarlo, mismo criterio que el resto de esta clase.
        public Task<(EstadoRbacResultado Estado, string? Origen)> LeerEstadoRbacConOrigenAsync(
            int clientId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<HallazgoResueltoFila>> LeerHallazgosResueltosAsync(
            int clientId, CancellationToken ct = default)
        {
            LeerHallazgosResueltosLlamadas++;
            return Task.FromResult<IReadOnlyList<HallazgoResueltoFila>>([]);
        }
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

    /// <summary>
    /// Fuente de reservas controlable, en memoria: por defecto responde "sin credenciales activas"
    /// para cualquier client_id (mismo estado que <c>ReservasRecolector.CapturarAsync</c> da sin
    /// filas en <c>client_azure_credentials</c>), así que ningún test paga una llamada real a Azure
    /// ni a SQL. <see cref="FallarLecturaDeCredenciales"/> y <see cref="ConCredencialYReservas"/>
    /// dejan que un test puntual configure, por client_id, el hueco que el controller tiene que
    /// cubrir (<c>ActiveCredentialsAsync</c> revienta) o el camino feliz (una credencial con
    /// reservas de verdad) sin afectar a los demás client_id de esta misma fixture compartida
    /// (<see cref="IClassFixture{TFixture}"/> es una sola instancia para toda la clase).
    ///
    /// <para><see cref="LlamadasACredenciales"/> cuenta las lecturas por client_id: es como se prueba
    /// que <c>/preview</c> ya NO entra a este camino. Un contador global no serviría, porque la
    /// fixture es compartida y otros tests sí lo recorren.</para>
    /// </summary>
    public sealed class FakeReservationServiceControlable : IReservationService
    {
        private readonly HashSet<int> _fallaCredenciales = [];
        private readonly Dictionary<int, IReadOnlyList<CredentialRef>> _credencialesPorCliente = new();
        private readonly Dictionary<int, IReadOnlyList<ReservationDto>> _reservasPorCredencial = new();
        private readonly Dictionary<int, int> _llamadasPorCliente = new();

        public void FallarLecturaDeCredenciales(int clientId) => _fallaCredenciales.Add(clientId);

        public int LlamadasACredenciales(int clientId) => _llamadasPorCliente.GetValueOrDefault(clientId);

        public void ConCredencialYReservas(int clientId, CredentialRef credencial, IReadOnlyList<ReservationDto> reservas)
        {
            _credencialesPorCliente[clientId] = [credencial];
            _reservasPorCredencial[credencial.CredentialId] = reservas;
        }

        public Task<IReadOnlyList<CredentialRef>> ActiveCredentialsAsync(int clientId, CancellationToken ct = default)
        {
            _llamadasPorCliente[clientId] = _llamadasPorCliente.GetValueOrDefault(clientId) + 1;
            return _fallaCredenciales.Contains(clientId)
                ? throw new InvalidOperationException("Fallo simulado leyendo credenciales de Azure.")
                : Task.FromResult(_credencialesPorCliente.GetValueOrDefault(clientId, (IReadOnlyList<CredentialRef>)[]));
        }

        public Task<(IReadOnlyList<ReservationDto> Reservations, IReadOnlyList<object> Errors)> FetchAllAsync(
            IReadOnlyList<CredentialRef> credentials, int alertDays, bool includeUtilization, CancellationToken ct = default)
        {
            var todas = credentials
                .SelectMany(c => _reservasPorCredencial.GetValueOrDefault(c.CredentialId, []))
                .ToList();
            return Task.FromResult<(IReadOnlyList<ReservationDto>, IReadOnlyList<object>)>((todas, []));
        }

        public Task<IReadOnlyDictionary<string, object?>> ListClientReservationsAsync(
            IReadOnlyList<CredentialRef> credentials, int alertDays, bool includeUtilization, CancellationToken ct = default)
            => throw new NotSupportedException("El recolector usa FetchAllAsync directo, no este wrapper de dict.");
    }

    /// <summary>Consumidores confirmados por reservation_id, en memoria. Sin entrada, responde vacío
    /// (reserva sin consumidores confirmados, no una falla de lectura) -- mismo criterio que
    /// <see cref="ReservasRecolectorTests"/>. <see cref="ConFallaDeConsumidores"/> simula el otro
    /// caso, el que hay que poder distinguir: la reserva existe pero Consumption no la deja leer.
    ///
    /// <para>La utilización se pide de a una reserva ya filtrada (el recolector nunca la pide dentro
    /// de la lista, ver el comentario de clase de <c>ReservasRecolector</c>), así que este falso la
    /// responde en vez de reventar.</para></summary>
    public sealed class FakeAzureReservationsClientControlable : IAzureReservationsClient
    {
        private readonly Dictionary<string, IReadOnlyList<ReservationConsumer>> _consumidoresPorReserva = new();
        private readonly HashSet<string> _fallaConsumidores = [];

        public void ConConsumidores(string reservationId, IReadOnlyList<ReservationConsumer> consumidores) =>
            _consumidoresPorReserva[reservationId] = consumidores;

        public void ConFallaDeConsumidores(string reservationId) => _fallaConsumidores.Add(reservationId);

        public Task<IReadOnlyList<ReservationDto>> FetchForCredentialAsync(
            int credentialId, int alertDays, DateOnly today, bool includeUtilization, CancellationToken ct = default)
            => throw new NotSupportedException("El recolector usa IReservationService.FetchAllAsync, no este método.");

        public Task<(string Last, string Avg7d)> GetUtilizationAsync(int credentialId, string reservationId, CancellationToken ct = default)
            => Task.FromResult(("80%", "75%"));

        public Task<IReadOnlyList<ReservationConsumer>> GetConsumersAsync(
            int credentialId, string reservationId, int days, CancellationToken ct = default) =>
            _fallaConsumidores.Contains(reservationId)
                ? throw new InvalidOperationException("403 Consumption (falla simulada).")
                : Task.FromResult(_consumidoresPorReserva.GetValueOrDefault(reservationId, []));
    }
}
