using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.Boletin;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests.Boletin;

/// <summary>
/// Pruebas de la evaluación de novedades POR CLIENTE (POST evaluar / GET / PUT decidir): store fake
/// en memoria (sin SQL/Azure reales — eso lo cubre el E2E manual + BoletinNovedadClienteStoreTests
/// para la lógica pura de mapeo), pero pasan por el pipeline MVC completo (auth, roles,
/// RequireModule, IAnalysisAccess, model binding). Cubren: 503 con IA apagada, separación
/// aprobadas/pendientes (rechazadas y no_aplica NUNCA en la respuesta), validación de estado del PUT,
/// y que el PUT resuelve el client_id dueño de la fila y verifica acceso ANTES de mutar (patrón
/// FindingStateOwner de OptimizationController). Espejo de BoletinNovedadApiTests.
/// </summary>
public sealed class BoletinNovedadClienteApiTests : IClassFixture<BoletinNovedadClienteApiTests.Factory>
{
    private readonly Factory _factory;
    public BoletinNovedadClienteApiTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string email, string role)
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        var token = BitJwt.Create(Factory.Secret, email, "Test User", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ---- POST /boletin/clients/{clientId}/novedades/evaluar ----

    [Fact]
    public async Task Evaluar_con_ia_apagada_devuelve_503()
    {
        _factory.Store.IaConfigurada = false;
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        try
        {
            var res = await client.PostAsync("/boletin/clients/1/novedades/evaluar", null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("La IA no está configurada; no es posible evaluar novedades.", body.GetProperty("detail").GetString());
        }
        finally { _factory.Store.IaConfigurada = true; }
    }

    [Fact]
    public async Task Evaluar_con_inventario_ilegible_devuelve_503()
    {
        _factory.Store.ThrowOnEvaluar = new InvalidOperationException("No se pudo leer el inventario del cliente.");
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        try
        {
            var res = await client.PostAsync("/boletin/clients/1/novedades/evaluar", null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("No se pudo leer el inventario del cliente.", body.GetProperty("detail").GetString());
        }
        finally { _factory.Store.ThrowOnEvaluar = null; }
    }

    [Fact]
    public async Task Evaluar_con_cero_subs_administradas_devuelve_400()
    {
        // Configuración del cliente (nada administrado), no un fallo transitorio de Azure: el
        // controller debe mapear BoletinNoManagedSubscriptionsException a 400, no al 503 genérico.
        _factory.Store.ThrowOnEvaluar = new BoletinNoManagedSubscriptionsException();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        try
        {
            var res = await client.PostAsync("/boletin/clients/1/novedades/evaluar", null);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("El cliente no tiene suscripciones administradas activas.", body.GetProperty("detail").GetString());
        }
        finally { _factory.Store.ThrowOnEvaluar = null; }
    }

    [Fact]
    public async Task Evaluar_devuelve_evaluadas_y_candidatas()
    {
        _factory.Store.Evaluadas = 3;
        _factory.Store.Candidatas = 5;
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsync("/boletin/clients/1/novedades/evaluar", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("evaluadas").GetInt32());
        Assert.Equal(5, body.GetProperty("candidatas").GetInt32());
    }

    [Fact]
    public async Task Evaluar_como_lector_devuelve_403()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.PostAsync("/boletin/clients/1/novedades/evaluar", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Evaluar_sin_acceso_al_cliente_devuelve_403()
    {
        var client = ClientFor("consultor-sin-acceso@bit.ec", Roles.Consultor);
        var res = await client.PostAsync("/boletin/clients/999/novedades/evaluar", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- GET /boletin/clients/{clientId}/novedades ----

    [Fact]
    public async Task Listar_separa_aprobadas_y_pendientes_y_nunca_devuelve_rechazadas_ni_no_aplica()
    {
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.GetAsync("/boletin/clients/1/novedades");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        var aprobadas = body.GetProperty("aprobadas");
        var pendientes = body.GetProperty("pendientes");
        Assert.Equal(1, aprobadas.GetArrayLength());
        Assert.Equal(1, pendientes.GetArrayLength());

        var aprobada = aprobadas[0];
        Assert.Equal("aprobada", aprobada.GetProperty("estado").GetString());
        Assert.Equal("Novedad aprobada", aprobada.GetProperty("titulo").GetString());
        var pendiente = pendientes[0];
        Assert.Equal("pendiente", pendiente.GetProperty("estado").GetString());
        Assert.Equal("Novedad pendiente", pendiente.GetProperty("titulo").GetString());
    }

    [Fact]
    public async Task Listar_shape_snake_case_completo()
    {
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.GetAsync("/boletin/clients/1/novedades");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var pendiente = body.GetProperty("pendientes")[0];

        foreach (var prop in new[]
                 {
                     "id", "novedad_id", "titulo", "titulo_es", "descripcion", "descripcion_es",
                     "link", "estado_feed", "categoria_bit", "published_at", "por_que", "estado",
                 })
            Assert.True(pendiente.TryGetProperty(prop, out _), $"falta la propiedad '{prop}'");

        Assert.Equal("pendiente", pendiente.GetProperty("estado").GetString());
        Assert.False(string.IsNullOrEmpty(pendiente.GetProperty("por_que").GetString()));
    }

    [Fact]
    public async Task Listar_sin_param_no_incluye_la_clave_rechazadas()
    {
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.GetAsync("/boletin/clients/1/novedades");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("rechazadas", out _));
    }

    [Fact]
    public async Task Listar_con_include_rechazadas_trae_la_fila_rechazada_con_decidido_por()
    {
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.GetAsync("/boletin/clients/1/novedades?include_rechazadas=true");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("rechazadas", out var rechazadas));
        var rechazada = Assert.Single(rechazadas.EnumerateArray());
        Assert.Equal("rechazada", rechazada.GetProperty("estado").GetString());
        Assert.Equal("consultor@bit.ec", rechazada.GetProperty("decidido_por").GetString());
        Assert.False(string.IsNullOrEmpty(rechazada.GetProperty("decidido_at").GetString()));

        // El shape de item es uno solo: aprobadas/pendientes siguen presentes igual que sin el param.
        Assert.Equal(1, body.GetProperty("aprobadas").GetArrayLength());
        Assert.Equal(1, body.GetProperty("pendientes").GetArrayLength());
    }

    [Fact]
    public async Task Listar_expone_recursos_parseado_y_null_cuando_no_hay()
    {
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.GetAsync("/boletin/clients/1/novedades");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // La pendiente (id 2) trae recursos sembrados; la aprobada (id 3) no trae.
        var pendiente = body.GetProperty("pendientes")[0];
        var recursos = pendiente.GetProperty("recursos");
        Assert.Equal(JsonValueKind.Array, recursos.ValueKind);
        Assert.Equal("Microsoft.Compute/virtualMachines", recursos[0].GetProperty("type").GetString());
        Assert.Equal(3, recursos[0].GetProperty("cantidad").GetInt32());

        var aprobada = body.GetProperty("aprobadas")[0];
        Assert.Equal(JsonValueKind.Null, aprobada.GetProperty("recursos").ValueKind);
    }

    [Fact]
    public async Task Listar_trae_ultima_evaluacion_y_feed_actualizado_del_store()
    {
        _factory.Store.Seed();
        _factory.Store.UltimaEvaluacion = new DateTime(2026, 8, 2, 15, 4, 5, DateTimeKind.Utc);
        _factory.Store.FeedActualizado = new DateTime(2026, 8, 4, 8, 0, 11, DateTimeKind.Utc);
        try
        {
            var client = ClientFor("consultor@bit.ec", Roles.Consultor);
            var res = await client.GetAsync("/boletin/clients/1/novedades");
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal("2026-08-02T15:04:05Z", body.GetProperty("ultima_evaluacion").GetString());
            Assert.Equal("2026-08-04T08:00:11Z", body.GetProperty("feed_actualizado").GetString());
        }
        finally { _factory.Store.UltimaEvaluacion = null; _factory.Store.FeedActualizado = null; }
    }

    [Fact]
    public async Task Listar_sin_metadatos_devuelve_null_en_ambos_campos()
    {
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.GetAsync("/boletin/clients/1/novedades");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Null, body.GetProperty("ultima_evaluacion").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("feed_actualizado").ValueKind);
    }

    [Fact]
    public async Task Listar_como_lector_devuelve_200()
    {
        _factory.Store.Seed();
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.GetAsync("/boletin/clients/1/novedades");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Listar_sin_acceso_al_cliente_devuelve_403()
    {
        var client = ClientFor("consultor-sin-acceso@bit.ec", Roles.Consultor);
        var res = await client.GetAsync("/boletin/clients/999/novedades");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- PUT /boletin/novedades-cliente/{id} ----

    [Fact]
    public async Task Put_estado_invalido_devuelve_400()
    {
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades-cliente/2", Json("{\"estado\":\"no_aplica\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("estado", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_sin_estado_devuelve_400()
    {
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades-cliente/2", Json("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_valido_devuelve_200_y_registra_actor()
    {
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades-cliente/2", Json("{\"estado\":\"aprobada\",\"por_que\":\"validado con el cliente\"}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var decision = Assert.Single(_factory.Store.Decisiones, d => d.Id == 2);
        Assert.Equal("aprobada", decision.Estado);
        Assert.Equal("validado con el cliente", decision.PorQue);
        Assert.Equal("consultor@bit.ec", decision.Actor);
        Assert.Equal(1, decision.ClientId);
    }

    [Fact]
    public async Task Put_id_inexistente_devuelve_404()
    {
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades-cliente/9999", Json("{\"estado\":\"aprobada\"}"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Put_sobre_fila_no_aplica_devuelve_404_y_no_muta()
    {
        // no_aplica (id 4, sembrada por Seed()) es un veredicto terminal de la IA e invisible (el GET
        // nunca lo lista) — un PUT con ese id adivinado no debe revivirlo. Owner sí existe (client 1,
        // accesible), así que esto ejercita el guard de DecidirAsync, no el chequeo de acceso.
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades-cliente/4", Json("{\"estado\":\"aprobada\"}"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain(_factory.Store.Decisiones, d => d.Id == 4);
        Assert.Equal("no_aplica", _factory.Store.Rows.Single(r => r.Estado.Id == 4).Estado.Estado);
    }

    [Fact]
    public async Task Put_sin_por_que_conserva_el_texto_de_la_ia()
    {
        // Revertir la fila 3 (aprobada, por_que="usas 2 App Service" puesto por la IA al evaluar) de
        // vuelta a pendiente SIN mandar por_que en el body no debe borrarlo.
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades-cliente/3", Json("{\"estado\":\"pendiente\"}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var decision = Assert.Single(_factory.Store.Decisiones, d => d.Id == 3);
        Assert.Equal("pendiente", decision.Estado);
        Assert.Equal("usas 2 App Service", decision.PorQue);
    }

    [Fact]
    public async Task Put_de_fila_de_otro_cliente_sin_acceso_devuelve_403_y_no_muta()
    {
        // La fila 2 pertenece al client_id 1: un consultor sin asignación a ese cliente NO debe
        // poder decidir sobre ella, aunque conozca el id de la fila (verifica pertenencia ANTES de mutar).
        _factory.Store.Seed();
        var client = ClientFor("consultor-sin-acceso@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades-cliente/2", Json("{\"estado\":\"aprobada\"}"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.DoesNotContain(_factory.Store.Decisiones, d => d.Id == 2);
    }

    [Fact]
    public async Task Put_como_lector_devuelve_403()
    {
        _factory.Store.Seed();
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.PutAsync("/boletin/novedades-cliente/2", Json("{\"estado\":\"aprobada\"}"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Put_con_por_que_no_string_devuelve_400_y_no_muta()
    {
        // Mismo bypass de tipos que ya rechazan BuildLifecycleFields/BuildNovedadFields: un por_que
        // numérico bien formado llegaba hasta GetString() y reventaba en 500 crudo (cazado en E2E).
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/boletin/novedades-cliente/2", Json("{\"estado\":\"aprobada\",\"por_que\":5}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("por_que", body.GetProperty("detail").GetString());
        Assert.DoesNotContain(_factory.Store.Decisiones, d => d.Id == 2);
    }

    [Fact]
    public async Task Put_con_utf8_invalido_devuelve_400_no_500()
    {
        // JsonDocument NO valida el UTF-8 de los strings al parsear: lo difiere hasta GetString(),
        // que lanza ante bytes inválidos (cliente con encoding cp1252 roto, cazado en E2E con curl).
        // Cuerpo malformado = 400, nunca 500 crudo.
        _factory.Store.Seed();
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("{\"estado\":\"pendiente\",\"por_que\":\"S"));
        bytes.Add(0xED); // "í" en cp1252: byte de arranque UTF-8 inválido en esta posición
        bytes.AddRange(Encoding.ASCII.GetBytes(" aplica\"}"));
        var content = new ByteArrayContent(bytes.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var res = await client.PutAsync("/boletin/novedades-cliente/2", content);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.DoesNotContain(_factory.Store.Decisiones, d => d.Id == 2);
    }

    // ---- Fixture: API real en memoria, solo se fake-an auth/acceso y el store de novedades-cliente ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeAnalysisAccessForNovedadCliente Access { get; } = new();
        public FakeBoletinNovedadClienteStore Store { get; } = new();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IAnalysisAccess>();
                services.AddSingleton<IAnalysisAccess>(Access);
                services.RemoveAll<IBoletinNovedadClienteStore>();
                services.AddSingleton<IBoletinNovedadClienteStore>(Store);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
            });
        }
    }

    /// <summary>Acceso en memoria: el cliente 1 es accesible para cualquier consultor cuyo email NO
    /// contenga "sin-acceso" (simula la asignación real vía dbo.user_client_assignment). admin no
    /// aplica en estas pruebas (no se usa ese rol acá).</summary>
    public sealed class FakeAnalysisAccessForNovedadCliente : IAnalysisAccess
    {
        public Task<IReadOnlySet<int>?> AccessibleClientIdsAsync(ClaimsPrincipal user, CancellationToken ct = default)
        {
            var email = user.FindFirst("sub")?.Value ?? "";
            IReadOnlySet<int> ids = email.Contains("sin-acceso") ? new HashSet<int>() : new HashSet<int> { 1 };
            return Task.FromResult<IReadOnlySet<int>?>(ids);
        }

        public Task<AccessCheck> AssertAnalysisAccessAsync(ClaimsPrincipal user, int analysisId, CancellationToken ct = default)
            => Task.FromResult(AccessCheck.NotFound("no aplica"));

        public Task<AccessCheck> AssertCostResultAccessAsync(ClaimsPrincipal user, int costResultId, CancellationToken ct = default)
            => Task.FromResult(AccessCheck.NotFound("no aplica"));

        public async Task<AccessCheck> AssertClientAccessAsync(ClaimsPrincipal user, int clientId, CancellationToken ct = default)
        {
            var ids = await AccessibleClientIdsAsync(user, ct);
            return ids!.Contains(clientId) ? AccessCheck.Allow() : AccessCheck.Forbidden();
        }

        public Task<AccessCheck> AssertFileAccessAsync(ClaimsPrincipal user, int fileId, CancellationToken ct = default)
            => Task.FromResult(AccessCheck.NotFound("no aplica"));
    }

    /// <summary>Store de evaluación por cliente en memoria (sin SQL/Azure) para probar el pipeline
    /// HTTP completo. El flujo real (inventario ARG, evaluador IA, upsert por fingerprint) lo cubren
    /// BoletinNovedadClienteStoreTests (lógica pura de mapeo) + el E2E manual.</summary>
    public sealed class FakeBoletinNovedadClienteStore : IBoletinNovedadClienteStore
    {
        public bool IaConfigurada { get; set; } = true;
        public Exception? ThrowOnEvaluar { get; set; }
        public int Evaluadas { get; set; }
        public int Candidatas { get; set; }
        public DateTime? UltimaEvaluacion { get; set; }
        public DateTime? FeedActualizado { get; set; }
        public List<(NovedadRow Novedad, NovedadClienteRow Estado)> Rows { get; } = [];
        public Dictionary<int, int> OwnerByRowId { get; } = new();
        public List<(int Id, int ClientId, string Estado, string? PorQue, string Actor)> Decisiones { get; } = [];

        /// <summary>Semilla: 4 estados posibles para el client_id 1 (id 1 rechazada, id 2 pendiente
        /// con recursos citados, id 3 aprobada, id 4 no_aplica) — el GET real solo devuelve la
        /// pendiente y la aprobada por defecto; la rechazada solo sale con include_rechazadas=true y
        /// la no_aplica jamás sale de acá (terminal, invisible siempre).</summary>
        public void Seed()
        {
            Rows.Clear();
            OwnerByRowId.Clear();
            Decisiones.Clear(); // aislamiento entre tests: el Factory/Store es compartido (IClassFixture)
            Rows.Add((Novedad(101, "guid-rechazada", "Novedad rechazada"), new NovedadClienteRow(1, 101, 1, "rechazada", null, "consultor@bit.ec", DateTime.UtcNow)));
            Rows.Add((Novedad(102, "guid-pendiente", "Novedad pendiente"), new NovedadClienteRow(
                2, 102, 1, "pendiente", "usas 3 Azure SQL Database", null, null,
                "[{\"type\":\"Microsoft.Compute/virtualMachines\",\"cantidad\":3}]")));
            Rows.Add((Novedad(103, "guid-aprobada", "Novedad aprobada"), new NovedadClienteRow(3, 103, 1, "aprobada", "usas 2 App Service", "consultor@bit.ec", DateTime.UtcNow)));
            Rows.Add((Novedad(104, "guid-no-aplica", "Novedad no aplica"), new NovedadClienteRow(4, 104, 1, "no_aplica", null, null, null)));
            foreach (var (_, estado) in Rows) OwnerByRowId[estado.Id] = estado.ClientId;
        }

        private static NovedadRow Novedad(int id, string guid, string titulo) => new(
            id, guid, titulo, null, "descripcion en ingles", null, "https://azure.microsoft.com/updates/" + guid,
            "launched", "resiliencia_plataforma", "[]", new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc), true);

        public Task<(int Evaluadas, int Candidatas)> EvaluarPendientesAsync(int clientId, CancellationToken ct = default)
        {
            if (ThrowOnEvaluar is not null) throw ThrowOnEvaluar;
            if (!IaConfigurada) throw new InvalidOperationException("La IA no está configurada; no es posible evaluar novedades.");
            return Task.FromResult((Evaluadas, Candidatas));
        }

        public Task<IReadOnlyList<(NovedadRow Novedad, NovedadClienteRow Estado)>> ListAsync(
            int clientId, bool includeRechazadas = false, CancellationToken ct = default)
            // Espejo del filtro SQL real: aprobada/pendiente siempre salen de acá, no_aplica nunca
            // (terminal, invisible siempre) y rechazada solo cuando includeRechazadas=true.
            => Task.FromResult<IReadOnlyList<(NovedadRow, NovedadClienteRow)>>(
                Rows.Where(r => r.Estado.ClientId == clientId &&
                    (r.Estado.Estado is "aprobada" or "pendiente" || (includeRechazadas && r.Estado.Estado == "rechazada")))
                    .ToList());

        public Task<(DateTime? UltimaEvaluacion, DateTime? FeedActualizado)> MetadatosAsync(int clientId, CancellationToken ct = default)
            => Task.FromResult((UltimaEvaluacion, FeedActualizado));

        public Task<int?> OwnerClientIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult(OwnerByRowId.TryGetValue(id, out var cid) ? (int?)cid : null);

        public Task<bool> DecidirAsync(int id, int clientId, string estado, string? porQue, bool setPorQue, string actor, CancellationToken ct = default)
        {
            if (!OwnerByRowId.TryGetValue(id, out var owner) || owner != clientId) return Task.FromResult(false);
            var idx = Rows.FindIndex(r => r.Estado.Id == id);
            if (idx < 0) return Task.FromResult(false);
            var (novedad, estadoActual) = Rows[idx];
            // Espejo del WHERE estado <> 'no_aplica' del store real: ese veredicto de la IA es
            // terminal, un PUT sobre esa fila no la muta y se ve igual que un id inexistente.
            if (estadoActual.Estado == NovedadClienteEstados.NoAplica) return Task.FromResult(false);
            // setPorQue=false conserva el por_que actual (ej. el texto que puso la IA al evaluar) en
            // vez de pisarlo con null — espejo de la CASE WHEN @setPorQue = 1 del UPDATE real.
            var porQueFinal = setPorQue ? porQue : estadoActual.PorQue;
            Rows[idx] = (novedad, estadoActual with { Estado = estado, PorQue = porQueFinal, DecididoPor = actor, DecididoAt = DateTime.UtcNow });
            Decisiones.Add((id, clientId, estado, porQueFinal, actor));
            return Task.FromResult(true);
        }
    }
}
