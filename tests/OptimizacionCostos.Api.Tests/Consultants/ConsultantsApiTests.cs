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
using OptimizacionCostos.Api.Features.Consultants;

namespace OptimizacionCostos.Api.Tests.Consultants;

/// <summary>
/// Pruebas del módulo Asignación de consultores: NO requieren SQL (store fake), pero SÍ
/// pasan por el pipeline MVC real (auth, roles, model binding, JSON snake_case). Espejo
/// del patrón de PolicyCatalogApiTests/WafAdminApiTests. A diferencia de los catálogos,
/// las mutaciones son SOLO admin (consultor y lector -> 403).
/// ⚠️ Solo datos inventados genéricos (Cliente Uno, Persona A...): nunca nombres reales.
/// </summary>
public sealed class ConsultantsApiTests : IClassFixture<ConsultantsApiTests.Factory>
{
    private readonly Factory _factory;
    public ConsultantsApiTests(Factory factory) => _factory = factory;

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

    private HttpClient Admin() => ClientFor("admin@bit.ec", Roles.Admin);

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ---- Listado con personas embebidas ----

    [Fact]
    public async Task Lista_devuelve_activas_ordenadas_con_personas_embebidas()
    {
        // Lectura para cualquier autenticado (lector incluido).
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var items = await client.GetFromJsonAsync<JsonElement>("/consultant-assignments");

        // Solo asignaciones activas.
        Assert.DoesNotContain(items.EnumerateArray(),
            a => a.GetProperty("client_name").GetString() == "Cliente Inactivo");

        // ORDER BY client_name.
        var names = items.EnumerateArray()
            .Select(a => a.GetProperty("client_name").GetString()).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);

        // Cliente Uno: principals/backups como {person_id, name} ordenados por nombre,
        // coordinator/comercial como objeto.
        var uno = items.EnumerateArray()
            .First(a => a.GetProperty("client_name").GetString() == "Cliente Uno");
        var principals = uno.GetProperty("principals").EnumerateArray().ToList();
        Assert.Equal(2, principals.Count);
        Assert.Equal(1, principals[0].GetProperty("person_id").GetInt32());
        Assert.Equal("Persona A", principals[0].GetProperty("name").GetString());
        Assert.Equal("Persona B", principals[1].GetProperty("name").GetString());
        var backups = uno.GetProperty("backups").EnumerateArray().ToList();
        Assert.Single(backups);
        Assert.Equal("Persona B", backups[0].GetProperty("name").GetString());
        Assert.Equal("Coordinador C", uno.GetProperty("coordinator").GetProperty("name").GetString());
        Assert.Equal("Comercial D", uno.GetProperty("comercial").GetProperty("name").GetString());

        // Cliente Dos: comercial null explícito (DefaultIgnoreCondition.Never).
        var dos = items.EnumerateArray()
            .First(a => a.GetProperty("client_name").GetString() == "Cliente Dos");
        Assert.Equal(JsonValueKind.Null, dos.GetProperty("comercial").ValueKind);
    }

    [Fact]
    public async Task Detalle_inexistente_devuelve_404_con_detail()
    {
        var res = await Admin().GetAsync("/consultant-assignments/999999");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Asignación no encontrada", body.GetProperty("detail").GetString());
    }

    // ---- Crear asignación (roles + validación) ----

    [Fact]
    public async Task Crear_sin_principal_ids_devuelve_400()
    {
        var res = await Admin().PostAsJsonAsync("/consultant-assignments",
            new { client_name = "Cliente Sin Principal" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Se requiere al menos un consultor principal", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Crear_con_principal_ids_vacio_devuelve_400()
    {
        var res = await Admin().PostAsJsonAsync("/consultant-assignments",
            new { client_name = "Cliente Sin Principal", principal_ids = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Se requiere al menos un consultor principal", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Crear_con_persona_inactiva_devuelve_400()
    {
        // Persona 5 = "Persona Inactiva" en el seed del fake.
        var res = await Admin().PostAsJsonAsync("/consultant-assignments",
            new { client_name = "Cliente Persona Inactiva", principal_ids = new[] { 5 } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Personas inválidas o inactivas: 5", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Consultor_no_puede_crear_asignacion()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsJsonAsync("/consultant-assignments",
            new { client_name = "Cliente Prohibido", principal_ids = new[] { 1 } });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Lector_no_puede_crear_asignacion()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.PostAsJsonAsync("/consultant-assignments",
            new { client_name = "Cliente Prohibido", principal_ids = new[] { 1 } });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Admin_crea_asignacion_completa()
    {
        var res = await Admin().PostAsJsonAsync("/consultant-assignments", new
        {
            client_name = "Cliente Nuevo Completo",
            service = "Servicio Uno",
            category = "ALTO",
            country = "QUITO",
            contract_end = "2027-01-31",
            principal_ids = new[] { 1 },
            backup_ids = new[] { 2 },
            coordinator_id = 3,
            comercial_id = 4,
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Asignación creada", body.GetProperty("message").GetString());
        var id = body.GetProperty("assignment_id").GetInt32();
        Assert.True(id > 0);

        var item = await Admin().GetFromJsonAsync<JsonElement>($"/consultant-assignments/{id}");
        Assert.Equal("Persona A", item.GetProperty("principals")[0].GetProperty("name").GetString());
        Assert.Equal("Persona B", item.GetProperty("backups")[0].GetProperty("name").GetString());
        Assert.Equal(3, item.GetProperty("coordinator").GetProperty("person_id").GetInt32());
        Assert.Equal(4, item.GetProperty("comercial").GetProperty("person_id").GetInt32());
    }

    // ---- Actualizar asignación (exclude_unset + reemplazo de conjuntos) ----

    [Fact]
    public async Task Put_parcial_de_escalares_no_toca_los_conjuntos()
    {
        var res = await Admin().PutAsync("/consultant-assignments/2",
            Json("{\"service\":\"Servicio Editado\",\"clave_desconocida\":\"ignorada\"}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Al store solo llegan las claves presentes en el body Y en la whitelist,
        // y los conjuntos van null (= no tocar).
        Assert.NotNull(_factory.Store.LastAssignmentUpdateFields);
        Assert.Single(_factory.Store.LastAssignmentUpdateFields!);
        Assert.Equal("Servicio Editado", _factory.Store.LastAssignmentUpdateFields!["service"]);
        Assert.Null(_factory.Store.LastPrincipalIds);
        Assert.Null(_factory.Store.LastBackupIds);

        // Los conjuntos del fake quedan intactos.
        var item = await Admin().GetFromJsonAsync<JsonElement>("/consultant-assignments/2");
        Assert.Equal("Servicio Editado", item.GetProperty("service").GetString());
        var principals = item.GetProperty("principals").EnumerateArray().ToList();
        Assert.Single(principals);
        Assert.Equal("Persona B", principals[0].GetProperty("name").GetString());
        Assert.Equal("Persona A", item.GetProperty("backups")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Put_con_principal_ids_reemplaza_el_conjunto()
    {
        var res = await Admin().PutAsync("/consultant-assignments/3", Json("{\"principal_ids\":[2]}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal([2], _factory.Store.LastPrincipalIds);
        Assert.Null(_factory.Store.LastBackupIds);

        var item = await Admin().GetFromJsonAsync<JsonElement>("/consultant-assignments/3");
        var principals = item.GetProperty("principals").EnumerateArray().ToList();
        Assert.Single(principals);
        Assert.Equal(2, principals[0].GetProperty("person_id").GetInt32());
        // El escalar no viajó en el body: queda intacto.
        Assert.Equal("Cliente Tres", item.GetProperty("client_name").GetString());
    }

    [Fact]
    public async Task Put_con_principal_ids_vacio_devuelve_400()
    {
        var res = await Admin().PutAsync("/consultant-assignments/1", Json("{\"principal_ids\":[]}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Se requiere al menos un consultor principal", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_sin_campos_conocidos_devuelve_400()
    {
        var res = await Admin().PutAsync("/consultant-assignments/1", Json("{\"clave_desconocida\":1}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("No fields to update", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_coordinator_id_null_explicito_limpia_el_campo()
    {
        var res = await Admin().PutAsync("/consultant-assignments/2", Json("{\"coordinator_id\":null}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.True(_factory.Store.LastAssignmentUpdateFields!.ContainsKey("coordinator_id"));
        Assert.Null(_factory.Store.LastAssignmentUpdateFields!["coordinator_id"]);

        var item = await Admin().GetFromJsonAsync<JsonElement>("/consultant-assignments/2");
        Assert.Equal(JsonValueKind.Null, item.GetProperty("coordinator").ValueKind);
    }

    [Fact]
    public async Task Consultor_no_puede_editar_asignacion()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PutAsync("/consultant-assignments/1", Json("{\"service\":\"x\"}"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- Eliminar asignación (soft-delete) ----

    [Fact]
    public async Task Delete_hace_soft_delete_no_borra_la_fila()
    {
        var res = await Admin().DeleteAsync("/consultant-assignments/4");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Asignación desactivada", body.GetProperty("message").GetString());
        Assert.Equal(4, body.GetProperty("assignment_id").GetInt32());

        // La fila sigue existiendo pero inactiva...
        var row = _factory.Store.FindAssignment(4)!;
        Assert.False(row.IsActive);

        // ...y el listado (solo activas) ya no la incluye.
        var items = await Admin().GetFromJsonAsync<JsonElement>("/consultant-assignments");
        Assert.DoesNotContain(items.EnumerateArray(),
            a => a.GetProperty("assignment_id").GetInt32() == 4);
    }

    [Fact]
    public async Task Delete_asignacion_como_consultor_devuelve_403()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.DeleteAsync("/consultant-assignments/1");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- Personas (directorio) ----

    [Fact]
    public async Task People_devuelve_todas_incluidas_inactivas_con_flag()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var items = await client.GetFromJsonAsync<JsonElement>("/consultant-assignments/people");

        var inactiva = items.EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "Persona Inactiva");
        Assert.False(inactiva.GetProperty("is_active").GetBoolean());
        Assert.Equal("consultor", inactiva.GetProperty("person_type").GetString());

        var activa = items.EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "Persona A");
        Assert.True(activa.GetProperty("is_active").GetBoolean());
    }

    [Fact]
    public async Task People_crud_completo_como_admin()
    {
        var admin = Admin();

        // Crear
        var res = await admin.PostAsJsonAsync("/consultant-assignments/people",
            new { name = "Persona Crud", person_type = "consultor", email = "persona.crud@example.com" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Persona creada", body.GetProperty("message").GetString());
        var id = body.GetProperty("person_id").GetInt32();
        Assert.True(id > 0);

        // Editar (exclude_unset: solo email viaja)
        res = await admin.PutAsync($"/consultant-assignments/people/{id}",
            Json("{\"email\":\"persona.crud2@example.com\"}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Single(_factory.Store.LastPersonUpdateFields!);
        Assert.Equal("persona.crud2@example.com", _factory.Store.LastPersonUpdateFields!["email"]);

        // Soft-delete: sigue en el directorio pero inactiva.
        res = await admin.DeleteAsync($"/consultant-assignments/people/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Persona desactivada", body.GetProperty("message").GetString());

        var items = await admin.GetFromJsonAsync<JsonElement>("/consultant-assignments/people");
        var row = items.EnumerateArray().First(p => p.GetProperty("person_id").GetInt32() == id);
        Assert.False(row.GetProperty("is_active").GetBoolean());
    }

    [Fact]
    public async Task Crear_persona_con_tipo_invalido_devuelve_400()
    {
        var res = await Admin().PostAsJsonAsync("/consultant-assignments/people",
            new { name = "Persona Tipo Malo", person_type = "gerente" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Tipo de persona inválido", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task People_mutaciones_como_consultor_o_lector_devuelven_403()
    {
        var consultor = ClientFor("consultor@bit.ec", Roles.Consultor);
        var lector = ClientFor("lector@bit.ec", Roles.Lector);

        var res = await consultor.PostAsJsonAsync("/consultant-assignments/people",
            new { name = "Persona Prohibida", person_type = "consultor" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        res = await lector.PutAsync("/consultant-assignments/people/1", Json("{\"name\":\"Otro\"}"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        res = await consultor.DeleteAsync("/consultant-assignments/people/1");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        res = await lector.PostAsJsonAsync("/consultant-assignments/people/1/reassign",
            new { to_person_id = 2 });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- Reasignación masiva ----

    [Fact]
    public async Task Reassign_default_cubre_todos_los_scopes_con_merge_sin_duplicados()
    {
        var admin = Admin();
        var origen = await CreatePersonAsync(admin, "Persona Origen Uno");
        var destino = await CreatePersonAsync(admin, "Persona Destino Uno");

        // La destino YA es principal junto a la origen -> merge (queda un solo vínculo).
        var a1 = await CreateAssignmentAsync(admin, "Cliente R Merge",
            principalIds: [origen, destino]);
        // Origen como principal Y backup -> ambos vínculos se mueven.
        var a2 = await CreateAssignmentAsync(admin, "Cliente R Doble",
            principalIds: [origen], backupIds: [origen]);
        // Origen como coordinadora -> UPDATE de columna.
        var a3 = await CreateAssignmentAsync(admin, "Cliente R Coord",
            principalIds: [destino], coordinatorId: origen);

        var res = await admin.PostAsJsonAsync($"/consultant-assignments/people/{origen}/reassign",
            new { to_person_id = destino });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Reasignación aplicada", body.GetProperty("message").GetString());
        Assert.Equal(3, body.GetProperty("changed_assignments").GetInt32());

        // Merge: un solo principal (la destino), sin duplicados.
        var i1 = await admin.GetFromJsonAsync<JsonElement>($"/consultant-assignments/{a1}");
        var p1 = i1.GetProperty("principals").EnumerateArray().ToList();
        Assert.Single(p1);
        Assert.Equal(destino, p1[0].GetProperty("person_id").GetInt32());

        var i2 = await admin.GetFromJsonAsync<JsonElement>($"/consultant-assignments/{a2}");
        Assert.Equal(destino, i2.GetProperty("principals")[0].GetProperty("person_id").GetInt32());
        Assert.Equal(destino, i2.GetProperty("backups")[0].GetProperty("person_id").GetInt32());

        var i3 = await admin.GetFromJsonAsync<JsonElement>($"/consultant-assignments/{a3}");
        Assert.Equal(destino, i3.GetProperty("coordinator").GetProperty("person_id").GetInt32());
    }

    [Fact]
    public async Task Reassign_con_scope_parcial_solo_toca_ese_rol()
    {
        var admin = Admin();
        var origen = await CreatePersonAsync(admin, "Persona Origen Dos");
        var destino = await CreatePersonAsync(admin, "Persona Destino Dos");
        var aid = await CreateAssignmentAsync(admin, "Cliente R Parcial",
            principalIds: [origen], backupIds: [origen], coordinatorId: origen);

        var res = await admin.PostAsJsonAsync($"/consultant-assignments/people/{origen}/reassign",
            new { to_person_id = destino, scopes = new[] { "backup" } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("changed_assignments").GetInt32());

        var item = await admin.GetFromJsonAsync<JsonElement>($"/consultant-assignments/{aid}");
        Assert.Equal(origen, item.GetProperty("principals")[0].GetProperty("person_id").GetInt32());
        Assert.Equal(destino, item.GetProperty("backups")[0].GetProperty("person_id").GetInt32());
        Assert.Equal(origen, item.GetProperty("coordinator").GetProperty("person_id").GetInt32());
    }

    [Fact]
    public async Task Reassign_permite_origen_inactivo()
    {
        // Flujo operativo real: la persona sale -> se desactiva -> se reasignan sus clientes.
        var admin = Admin();
        var origen = await CreatePersonAsync(admin, "Persona Origen Saliente");
        var destino = await CreatePersonAsync(admin, "Persona Destino Tres");
        var aid = await CreateAssignmentAsync(admin, "Cliente R Saliente", principalIds: [origen]);

        // Se desactiva la persona origen (soft-delete)...
        var res = await admin.DeleteAsync($"/consultant-assignments/people/{origen}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // ...y la reasignación procede normal.
        res = await admin.PostAsJsonAsync($"/consultant-assignments/people/{origen}/reassign",
            new { to_person_id = destino });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("changed_assignments").GetInt32());

        var item = await admin.GetFromJsonAsync<JsonElement>($"/consultant-assignments/{aid}");
        var principals = item.GetProperty("principals").EnumerateArray().ToList();
        Assert.Single(principals);
        Assert.Equal(destino, principals[0].GetProperty("person_id").GetInt32());
    }

    [Fact]
    public async Task Reassign_origen_igual_destino_devuelve_400()
    {
        var res = await Admin().PostAsJsonAsync("/consultant-assignments/people/1/reassign",
            new { to_person_id = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("La persona origen y destino son la misma", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Reassign_persona_origen_inexistente_devuelve_404()
    {
        var res = await Admin().PostAsJsonAsync("/consultant-assignments/people/999999/reassign",
            new { to_person_id = 1 });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Persona no encontrada", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Reassign_destino_inexistente_o_inactivo_devuelve_400()
    {
        var res = await Admin().PostAsJsonAsync("/consultant-assignments/people/1/reassign",
            new { to_person_id = 999999 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Persona destino inválida o inactiva", body.GetProperty("detail").GetString());

        // Persona 5 existe pero está inactiva -> también 400.
        res = await Admin().PostAsJsonAsync("/consultant-assignments/people/1/reassign",
            new { to_person_id = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Reassign_con_scope_invalido_devuelve_400()
    {
        var res = await Admin().PostAsJsonAsync("/consultant-assignments/people/1/reassign",
            new { to_person_id = 2, scopes = new[] { "gerente" } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- Helpers ----

    private static async Task<int> CreatePersonAsync(HttpClient client, string name, string type = "consultor")
    {
        var res = await client.PostAsJsonAsync("/consultant-assignments/people",
            new { name, person_type = type });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("person_id").GetInt32();
    }

    private static async Task<int> CreateAssignmentAsync(HttpClient client, string clientName,
        int[] principalIds, int[]? backupIds = null, int? coordinatorId = null, int? comercialId = null)
    {
        var res = await client.PostAsJsonAsync("/consultant-assignments", new
        {
            client_name = clientName,
            principal_ids = principalIds,
            backup_ids = backupIds,
            coordinator_id = coordinatorId,
            comercial_id = comercialId,
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("assignment_id").GetInt32();
    }

    // ---- Fixture: API real en memoria, solo se fake-an auth y el store ----

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeConsultantsStore Store { get; } = new FakeConsultantsStore().Seed();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IConsultantsStore>();
                services.AddSingleton<IConsultantsStore>(Store);
            });
        }
    }

    /// <summary>
    /// Store en memoria (sin BD) con la misma semántica que SqlConsultantsStore:
    /// listado solo activas ordenado por cliente, personas embebidas ordenadas por
    /// nombre, reemplazo de conjuntos y reassign con merge.
    /// </summary>
    public sealed class FakeConsultantsStore : IConsultantsStore
    {
        private readonly List<PersonItem> _people = [];
        private readonly List<FakeAssignment> _assignments = [];
        private int _personSeq;
        private int _assignmentSeq;

        public IReadOnlyDictionary<string, object?>? LastAssignmentUpdateFields { get; private set; }
        public IReadOnlyList<int>? LastPrincipalIds { get; private set; }
        public IReadOnlyList<int>? LastBackupIds { get; private set; }
        public IReadOnlyDictionary<string, object?>? LastPersonUpdateFields { get; private set; }

        public sealed class FakeAssignment
        {
            public int AssignmentId { get; init; }
            public string ClientName { get; set; } = "";
            public string? Service { get; set; }
            public string? Category { get; set; }
            public string? Country { get; set; }
            public string? Status { get; set; } = "ACTIVO";
            public string? Observations { get; set; }
            public int? CoordinatorId { get; set; }
            public int? ComercialId { get; set; }
            public bool IsActive { get; set; } = true;
            public List<int> PrincipalIds { get; } = [];
            public List<int> BackupIds { get; } = [];
        }

        public FakeConsultantsStore Seed()
        {
            AddPerson("Persona A", "consultor");           // 1
            AddPerson("Persona B", "consultor");           // 2
            AddPerson("Coordinador C", "coordinador");     // 3
            AddPerson("Comercial D", "comercial");         // 4
            AddPerson("Persona Inactiva", "consultor", active: false); // 5

            AddAssignment("Cliente Uno", [2, 1], [2], 3, 4, "ALTO");        // 1 (solo lectura en tests)
            AddAssignment("Cliente Dos", [2], [1], 3, null, "MEDIO");       // 2 (PUT escalares)
            AddAssignment("Cliente Tres", [1], [], null, null, "BAJO");     // 3 (PUT principal_ids)
            AddAssignment("Cliente Cuatro", [2], [], 3, null, "BAJO");      // 4 (DELETE soft)
            AddAssignment("Cliente Inactivo", [1], [], null, null, "ALTO", isActive: false); // 5
            return this;
        }

        public FakeAssignment? FindAssignment(int id) => _assignments.FirstOrDefault(a => a.AssignmentId == id);
        public PersonItem? FindPerson(int id) => _people.FirstOrDefault(p => p.PersonId == id);

        private int AddPerson(string name, string type, bool active = true, string? email = null)
        {
            var person = new PersonItem
            {
                PersonId = ++_personSeq, Name = name, Email = email,
                PersonType = type, IsActive = active,
            };
            _people.Add(person);
            return person.PersonId;
        }

        private void AddAssignment(string clientName, int[] principals, int[] backups,
            int? coordinatorId, int? comercialId, string category, bool isActive = true)
        {
            var a = new FakeAssignment
            {
                AssignmentId = ++_assignmentSeq, ClientName = clientName, Category = category,
                CoordinatorId = coordinatorId, ComercialId = comercialId, IsActive = isActive,
            };
            a.PrincipalIds.AddRange(principals);
            a.BackupIds.AddRange(backups);
            _assignments.Add(a);
        }

        // ---- Personas ----

        public Task<IReadOnlyList<PersonItem>> ListPeopleAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PersonItem>>(
                _people.OrderBy(p => p.Name, StringComparer.Ordinal).ToList());

        public Task<PersonItem?> GetPersonAsync(int personId, CancellationToken ct = default)
            => Task.FromResult(FindPerson(personId));

        public Task<int> CreatePersonAsync(PersonCreate data, CancellationToken ct = default)
            => Task.FromResult(AddPerson(data.Name, data.PersonType, email: data.Email));

        public Task<bool> UpdatePersonAsync(int personId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
        {
            LastPersonUpdateFields = fields;
            var idx = _people.FindIndex(p => p.PersonId == personId);
            if (idx < 0) return Task.FromResult(false);

            var item = _people[idx];
            if (fields.TryGetValue("name", out var name) && name is string n) item = item with { Name = n };
            if (fields.TryGetValue("email", out var email)) item = item with { Email = email as string };
            if (fields.TryGetValue("person_type", out var type) && type is string t) item = item with { PersonType = t };
            if (fields.TryGetValue("is_active", out var active) && active is bool b) item = item with { IsActive = b };
            _people[idx] = item;
            return Task.FromResult(true);
        }

        public Task<bool> SoftDeletePersonAsync(int personId, CancellationToken ct = default)
        {
            var idx = _people.FindIndex(p => p.PersonId == personId);
            if (idx < 0) return Task.FromResult(false);
            _people[idx] = _people[idx] with { IsActive = false };
            return Task.FromResult(true);
        }

        public Task<IReadOnlySet<int>> GetActivePersonIdsAsync(IReadOnlyCollection<int> personIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<int>>(
                _people.Where(p => p.IsActive && personIds.Contains(p.PersonId))
                    .Select(p => p.PersonId).ToHashSet());

        // ---- Asignaciones ----

        public Task<IReadOnlyList<AssignmentItem>> ListAssignmentsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AssignmentItem>>(
                _assignments.Where(a => a.IsActive)
                    .OrderBy(a => a.ClientName, StringComparer.Ordinal)
                    .Select(ToItem).ToList());

        public Task<AssignmentItem?> GetAssignmentAsync(int assignmentId, CancellationToken ct = default)
        {
            var a = FindAssignment(assignmentId);
            return Task.FromResult(a is null ? null : ToItem(a));
        }

        public Task<int> CreateAssignmentAsync(AssignmentCreate data, CancellationToken ct = default)
        {
            var a = new FakeAssignment
            {
                AssignmentId = ++_assignmentSeq,
                ClientName = data.ClientName,
                Service = data.Service,
                Category = data.Category,
                Country = data.Country,
                Status = data.Status ?? "ACTIVO",
                Observations = data.Observations,
                CoordinatorId = data.CoordinatorId,
                ComercialId = data.ComercialId,
            };
            a.PrincipalIds.AddRange((data.PrincipalIds ?? []).Distinct());
            a.BackupIds.AddRange((data.BackupIds ?? []).Distinct());
            _assignments.Add(a);
            return Task.FromResult(a.AssignmentId);
        }

        public Task<bool> UpdateAssignmentAsync(int assignmentId, IReadOnlyDictionary<string, object?> fields,
            IReadOnlyList<int>? principalIds, IReadOnlyList<int>? backupIds, CancellationToken ct = default)
        {
            LastAssignmentUpdateFields = fields;
            LastPrincipalIds = principalIds;
            LastBackupIds = backupIds;

            var a = FindAssignment(assignmentId);
            if (a is null) return Task.FromResult(false);

            if (fields.TryGetValue("client_name", out var cn) && cn is string s) a.ClientName = s;
            if (fields.TryGetValue("service", out var sv)) a.Service = sv as string;
            if (fields.TryGetValue("category", out var cat)) a.Category = cat as string;
            if (fields.TryGetValue("country", out var co)) a.Country = co as string;
            if (fields.TryGetValue("status", out var st)) a.Status = st as string;
            if (fields.TryGetValue("observations", out var ob)) a.Observations = ob as string;
            if (fields.TryGetValue("coordinator_id", out var coord)) a.CoordinatorId = coord as int?;
            if (fields.TryGetValue("comercial_id", out var com)) a.ComercialId = com as int?;

            if (principalIds is not null)
            {
                a.PrincipalIds.Clear();
                a.PrincipalIds.AddRange(principalIds.Distinct());
            }
            if (backupIds is not null)
            {
                a.BackupIds.Clear();
                a.BackupIds.AddRange(backupIds.Distinct());
            }
            return Task.FromResult(true);
        }

        public Task<bool> SoftDeleteAssignmentAsync(int assignmentId, CancellationToken ct = default)
        {
            var a = FindAssignment(assignmentId);
            if (a is null) return Task.FromResult(false);
            a.IsActive = false;
            return Task.FromResult(true);
        }

        // ---- Reasignación masiva (misma semántica de merge que el SQL) ----

        public Task<int> ReassignAsync(int fromPersonId, int toPersonId, IReadOnlyList<string> scopes, CancellationToken ct = default)
        {
            var changed = new HashSet<int>();
            foreach (var a in _assignments.Where(x => x.IsActive))
            {
                if (scopes.Contains("principal") && MoveLink(a.PrincipalIds, fromPersonId, toPersonId))
                    changed.Add(a.AssignmentId);
                if (scopes.Contains("backup") && MoveLink(a.BackupIds, fromPersonId, toPersonId))
                    changed.Add(a.AssignmentId);
                if (scopes.Contains("coordinador") && a.CoordinatorId == fromPersonId)
                {
                    a.CoordinatorId = toPersonId;
                    changed.Add(a.AssignmentId);
                }
                if (scopes.Contains("comercial") && a.ComercialId == fromPersonId)
                {
                    a.ComercialId = toPersonId;
                    changed.Add(a.AssignmentId);
                }
            }
            return Task.FromResult(changed.Count);
        }

        /// <summary>Mueve el vínculo origen->destino; si la destino ya está, solo elimina (merge).</summary>
        private static bool MoveLink(List<int> ids, int fromId, int toId)
        {
            if (!ids.Remove(fromId)) return false;
            if (!ids.Contains(toId)) ids.Add(toId);
            return true;
        }

        // ---- Helpers ----

        private AssignmentItem ToItem(FakeAssignment a) => new()
        {
            AssignmentId = a.AssignmentId,
            ClientName = a.ClientName,
            Service = a.Service,
            Category = a.Category,
            Country = a.Country,
            Status = a.Status,
            Observations = a.Observations,
            IsActive = a.IsActive,
            Coordinator = Ref(a.CoordinatorId),
            Comercial = Ref(a.ComercialId),
            Principals = Refs(a.PrincipalIds),
            Backups = Refs(a.BackupIds),
        };

        private PersonRef? Ref(int? personId)
            => personId is null ? null : new PersonRef(personId.Value, FindPerson(personId.Value)?.Name ?? "");

        private List<PersonRef> Refs(IEnumerable<int> ids)
            => ids.Select(id => new PersonRef(id, FindPerson(id)?.Name ?? ""))
                .OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }
}
