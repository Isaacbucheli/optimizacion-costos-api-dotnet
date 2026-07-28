using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.Pendientes;

namespace OptimizacionCostos.Api.Tests.Pendientes;

/// <summary>
/// Módulo Pendientes y bloqueantes: sin SQL (store fake) pero con el pipeline MVC real
/// (auth, permisos por módulo, binding, JSON snake_case).
///
/// Lo específico de este módulo, y por eso los tests: el permiso depende del {area} de la ruta
/// (dos módulos distintos), el dato vive en OTRA base que la SWA del tablero sigue escribiendo
/// (de ahí la concurrencia optimista y el 503 cuando no está configurada) y el autor de una nota
/// lo pone el backend.
///
/// ⚠️ Solo datos inventados genéricos (Cliente Uno, Cliente Dos...): nunca nombres reales.
/// </summary>
public sealed class PendientesApiTests : IClassFixture<PendientesApiTests.Factory>
{
    private readonly Factory _factory;

    // El store fake es un singleton del host (class fixture) y varios tests escriben, así que se
    // re-siembra antes de CADA test: xUnit no garantiza el orden dentro de la clase.
    public PendientesApiTests(Factory factory)
    {
        _factory = factory;
        _factory.Store.Reset();
    }

    private HttpClient ClientFor(string email, string role, string name = "Test User")
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BitJwt.Create(Factory.Secret, email, name, role));
        return client;
    }

    private HttpClient Admin(string name = "Test User") => ClientFor("admin@bit.ec", Roles.Admin, name);

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ---- Lectura ----

    [Fact]
    public async Task Get_area_devuelve_clientes_y_pendientes_con_historial()
    {
        var body = await Admin().GetFromJsonAsync<JsonElement>("/pendientes/CDC");

        Assert.Equal("CDC", body.GetProperty("area").GetString());
        Assert.Equal(2, body.GetProperty("clientes").GetArrayLength());

        var item = body.GetProperty("pendientes").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "p1");
        Assert.Equal(1, item.GetProperty("cliente_num").GetInt32());
        Assert.Equal("BLOQUEANTE", item.GetProperty("tipo").GetString());
        Assert.Equal(3, item.GetProperty("historial").GetArrayLength());
    }

    [Fact]
    public async Task Historial_respeta_Orden_de_insercion_no_la_fecha()
    {
        var body = await Admin().GetFromJsonAsync<JsonElement>("/pendientes/CDC");
        var notas = body.GetProperty("pendientes").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "p1")
            .GetProperty("historial").EnumerateArray().ToList();

        // El seed tiene fechas desordenadas a propósito (como el dato real del tablero).
        Assert.Equal([0, 1, 2], notas.Select(n => n.GetProperty("orden").GetInt32()).ToArray());
        Assert.Equal(
            ["2026-07-06", "2026-07-03", "2026-07-20"],
            notas.Select(n => n.GetProperty("fecha").GetString()).ToArray());
    }

    [Fact]
    public async Task Titulo_vacio_y_nota_sin_autor_viajan_tal_cual()
    {
        var body = await Admin().GetFromJsonAsync<JsonElement>("/pendientes/CDC");
        var item = body.GetProperty("pendientes").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "p1");

        // 84 de 85 pendientes reales no tienen título: no se inventa texto.
        Assert.Equal(JsonValueKind.Null, item.GetProperty("titulo").ValueKind);
        var sinAutor = item.GetProperty("historial")[2];
        Assert.Equal(JsonValueKind.Null, sinAutor.GetProperty("autor").ValueKind);
    }

    [Fact]
    public async Task Area_invalida_es_400_no_403()
    {
        var res = await Admin().GetAsync("/pendientes/PROD");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Area_en_minusculas_funciona()
    {
        var res = await Admin().GetAsync("/pendientes/infra");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ---- Permisos ----

    [Fact]
    public async Task Cada_area_se_permite_por_separado()
    {
        _factory.Perms.Set(Roles.Consultor, Modules.PendientesCdc, canView: false, canEdit: false);
        _factory.InvalidatePerms();
        try
        {
            var client = ClientFor("consultor-areas@bit.ec", Roles.Consultor);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/pendientes/CDC")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/pendientes/INFRA")).StatusCode);
        }
        finally
        {
            _factory.Perms.Set(Roles.Consultor, Modules.PendientesCdc, canView: true, canEdit: true);
            _factory.InvalidatePerms();
        }
    }

    [Fact]
    public async Task Lector_ve_pero_no_escribe()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/pendientes/CDC")).StatusCode);

        var res = await client.PostAsync("/pendientes/CDC/items",
            Json("""{"cliente_num":1,"descripcion":"algo"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Sin_token_es_401()
    {
        var res = await _factory.CreateClient().GetAsync("/pendientes/CDC");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- Alta y edición ----

    [Fact]
    public async Task Crear_asigna_id_propio_y_defaults()
    {
        var res = await Admin().PostAsync("/pendientes/CDC/items",
            Json("""{"cliente_num":2,"descripcion":"Pendiente nuevo"}"""));
        res.EnsureSuccessStatusCode();

        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));

        var item = await Admin().GetFromJsonAsync<JsonElement>($"/pendientes/CDC/items/{id}");
        Assert.Equal("PENDIENTE", item.GetProperty("tipo").GetString());
        Assert.Equal("MEDIA", item.GetProperty("prioridad").GetString());
        Assert.Equal("ABIERTO", item.GetProperty("estado").GetString());
    }

    [Fact]
    public async Task Crear_normaliza_en_progreso_con_espacio()
    {
        var res = await Admin().PostAsync("/pendientes/CDC/items",
            Json("""{"cliente_num":1,"descripcion":"En curso","estado":"en progreso"}"""));
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var item = await Admin().GetFromJsonAsync<JsonElement>($"/pendientes/CDC/items/{id}");
        Assert.Equal("EN_PROGRESO", item.GetProperty("estado").GetString());
    }

    [Fact]
    public async Task Crear_con_estado_fuera_de_lista_es_400()
    {
        var res = await Admin().PostAsync("/pendientes/CDC/items",
            Json("""{"cliente_num":1,"descripcion":"x","estado":"REABIERTO"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Crear_sin_descripcion_ni_titulo_es_400()
    {
        var res = await Admin().PostAsync("/pendientes/CDC/items", Json("""{"cliente_num":1}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Crear_con_cliente_de_otra_area_es_400()
    {
        // El cliente 9 solo existe en INFRA: sin FK en la BD, la integridad la valida la app.
        var res = await Admin().PostAsync("/pendientes/CDC/items",
            Json("""{"cliente_num":9,"descripcion":"x"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Editar_sin_el_token_de_concurrencia_es_400()
    {
        var res = await Admin().PutAsync("/pendientes/CDC/items/p2",
            Json("""{"cliente_num":1,"descripcion":"editado"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Editar_con_token_viejo_es_409()
    {
        var res = await Admin().PutAsync("/pendientes/CDC/items/p2",
            Json("""{"cliente_num":1,"descripcion":"editado","actualizado":"2020-01-01T00:00:00Z"}"""));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Editar_con_el_token_vigente_actualiza()
    {
        var before = await Admin().GetFromJsonAsync<JsonElement>("/pendientes/CDC/items/p2");
        var token = before.GetProperty("actualizado").GetString();

        var res = await Admin().PutAsync("/pendientes/CDC/items/p2",
            Json($$"""{"cliente_num":1,"descripcion":"editado","estado":"CERRADO","actualizado":"{{token}}"}"""));
        res.EnsureSuccessStatusCode();

        var after = await Admin().GetFromJsonAsync<JsonElement>("/pendientes/CDC/items/p2");
        Assert.Equal("editado", after.GetProperty("descripcion").GetString());
        Assert.Equal("CERRADO", after.GetProperty("estado").GetString());
        Assert.NotEqual(token, after.GetProperty("actualizado").GetString());
    }

    [Fact]
    public async Task Editar_un_id_inexistente_es_404()
    {
        var res = await Admin().PutAsync("/pendientes/CDC/items/no-existe",
            Json("""{"cliente_num":1,"descripcion":"x","actualizado":"2026-07-01T00:00:00Z"}"""));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ---- Notas ----

    [Fact]
    public async Task Nota_usa_el_autor_de_la_sesion_e_ignora_el_del_body()
    {
        var client = Admin(name: "Persona A");
        var res = await client.PostAsync("/pendientes/CDC/items/p2/notas",
            Json("""{"nota":"  avance del dia  ","autor":"Otro Cualquiera"}"""));
        res.EnsureSuccessStatusCode();

        var item = await Admin().GetFromJsonAsync<JsonElement>("/pendientes/CDC/items/p2");
        var ultima = item.GetProperty("historial").EnumerateArray().Last();
        Assert.Equal("Persona A", ultima.GetProperty("autor").GetString());
        Assert.Equal("avance del dia", ultima.GetProperty("nota").GetString());
    }

    [Fact]
    public async Task Nota_nueva_va_al_final_con_Orden_maximo_mas_uno()
    {
        // p3 tiene una sola nota con Orden 7 (hueco a propósito): MAX+1 = 8, no COUNT = 1.
        var res = await Admin().PostAsync("/pendientes/CDC/items/p3/notas", Json("""{"nota":"otra"}"""));
        res.EnsureSuccessStatusCode();

        var item = await Admin().GetFromJsonAsync<JsonElement>("/pendientes/CDC/items/p3");
        Assert.Equal(8, item.GetProperty("historial").EnumerateArray().Last().GetProperty("orden").GetInt32());
    }

    [Fact]
    public async Task Nota_vacia_es_400()
    {
        var res = await Admin().PostAsync("/pendientes/CDC/items/p2/notas", Json("""{"nota":"   "}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Nota_en_pendiente_inexistente_es_404()
    {
        var res = await Admin().PostAsync("/pendientes/CDC/items/no-existe/notas", Json("""{"nota":"x"}"""));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Borrar_una_nota_de_otro_pendiente_es_404()
    {
        // 101 pertenece a p1: pedirlo bajo p2 no debe borrar nada.
        var res = await Admin().DeleteAsync("/pendientes/CDC/items/p2/notas/101");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        var p1 = await Admin().GetFromJsonAsync<JsonElement>("/pendientes/CDC/items/p1");
        Assert.Equal(3, p1.GetProperty("historial").GetArrayLength());
    }

    [Fact]
    public async Task Borrar_la_nota_correcta_funciona()
    {
        var res = await Admin().DeleteAsync("/pendientes/CDC/items/p1/notas/102");
        res.EnsureSuccessStatusCode();

        var p1 = await Admin().GetFromJsonAsync<JsonElement>("/pendientes/CDC/items/p1");
        Assert.Equal(2, p1.GetProperty("historial").GetArrayLength());
        Assert.DoesNotContain(p1.GetProperty("historial").EnumerateArray(),
            n => n.GetProperty("hist_id").GetInt32() == 102);
    }

    // ---- Catálogo de clientes ----

    [Fact]
    public async Task Borrar_cliente_con_pendientes_es_409()
    {
        var res = await Admin().DeleteAsync("/pendientes/CDC/clientes/1");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Alta_de_cliente_asigna_el_siguiente_num_del_area()
    {
        var res = await Admin().PostAsync("/pendientes/INFRA/clientes",
            Json("""{"cliente":"Cliente Nuevo","categoria":"medio"}"""));
        res.EnsureSuccessStatusCode();

        var num = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("num").GetInt32();
        Assert.Equal(10, num); // el seed de INFRA llega al 9

        var payload = await Admin().GetFromJsonAsync<JsonElement>("/pendientes/INFRA");
        var creado = payload.GetProperty("clientes").EnumerateArray()
            .Single(c => c.GetProperty("num").GetInt32() == 10);
        Assert.Equal("MEDIO", creado.GetProperty("categoria").GetString());
    }

    [Fact]
    public async Task Categoria_invalida_es_400()
    {
        var res = await Admin().PostAsync("/pendientes/INFRA/clientes",
            Json("""{"cliente":"Cliente X","categoria":"CRITICO"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Cliente_sin_nombre_es_400()
    {
        var res = await Admin().PostAsync("/pendientes/INFRA/clientes", Json("""{"cliente":"  "}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- Módulo apagado ----

    [Fact]
    public async Task Sin_configuracion_de_BD_responde_503()
    {
        using var apagado = new Factory { Configured = false };
        apagado.Directory.Add("admin@bit.ec", Roles.Admin);
        var client = apagado.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BitJwt.Create(Factory.Secret, "admin@bit.ec", "Test User", Roles.Admin));

        var res = await client.GetAsync("/pendientes/CDC");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    // ---------- Infra de test ----------

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;

        /// <summary>
        /// False simula que faltan las SQL_*2 (módulo apagado). Se lee en cada request, así que basta
        /// fijarlo antes de la llamada. Un solo constructor público: xUnit lo exige para IClassFixture.
        /// </summary>
        public bool Configured { get; set; } = true;

        public FakeUserDirectory Directory { get; } = new();
        public FakePendientesStore Store { get; } = new FakePendientesStore().Seed();
        public FakeModulePermissionStore Perms { get; } = new FakeModulePermissionStore().SeedDefaults();
        /// <summary>
        /// El servicio de permisos es scoped: hay que pedirlo dentro de un scope (el root provider
        /// valida scopes y lanza). La caché que limpia es singleton, así que cualquier instancia sirve.
        /// </summary>
        public void InvalidatePerms()
        {
            using var scope = Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IModulePermissionService>().Invalidate();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("JWT_SECRET", Secret);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IPendientesStore>();
                services.AddSingleton<IPendientesStore>(Store);
                services.RemoveAll<ISeguimientoSqlConnectionFactory>();
                services.AddSingleton<ISeguimientoSqlConnectionFactory>(
                    new FakeSeguimientoFactory(() => Configured));
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(Perms);
            });
        }
    }

    /// <summary>Solo importa <see cref="IsConfigured"/>: el store real nunca corre en estos tests.</summary>
    private sealed class FakeSeguimientoFactory(Func<bool> configured) : ISeguimientoSqlConnectionFactory
    {
        public bool IsConfigured => configured();

        public Task<SqlConnection> OpenAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Los tests no abren conexiones reales");
    }

    /// <summary>
    /// Store en memoria con la misma semántica que <see cref="SqlPendientesStore"/>: Orden = MAX+1,
    /// concurrencia por Actualizado, nada de cascada al borrar clientes y área como parte de la clave.
    /// </summary>
    public sealed class FakePendientesStore : IPendientesStore
    {
        private sealed class Row
        {
            public required string Area { get; init; }
            public required PendienteItem Item { get; set; }
            public List<PendienteNota> Notas { get; } = [];
        }

        private readonly List<(string Area, PendienteCliente Cliente)> _clientes = [];
        private readonly List<Row> _rows = [];
        private int _histId = 200;
        private int _idSeq;

        /// <summary>Vuelve al estado sembrado: lo llama el constructor de la clase de tests.</summary>
        public void Reset()
        {
            _clientes.Clear();
            _rows.Clear();
            _histId = 200;
            _idSeq = 0;
            Seed();
        }

        public FakePendientesStore Seed()
        {
            _clientes.Add(("CDC", new PendienteCliente
            {
                Num = 1, Cliente = "Cliente Uno", Servicio = "SERVICIO A", Categoria = "ALTO",
                Pais = "PAIS A", Coordinador = "Persona B", Consultor = "Persona C",
            }));
            _clientes.Add(("CDC", new PendienteCliente { Num = 2, Cliente = "Cliente Dos" }));
            _clientes.Add(("INFRA", new PendienteCliente { Num = 9, Cliente = "Cliente Nueve" }));

            var p1 = new Row
            {
                Area = "CDC",
                Item = new PendienteItem
                {
                    Id = "p1", ClienteNum = 1, Titulo = null, Descripcion = "Bloqueante de ejemplo",
                    Tipo = "BLOQUEANTE", Prioridad = "ALTA", Estado = "ABIERTO",
                    Responsable = "Persona C", FechaCreacion = new DateOnly(2026, 6, 12),
                    Actualizado = new DateTime(2026, 7, 27, 15, 51, 28, DateTimeKind.Utc),
                },
            };
            // Fechas fuera de orden a propósito: el timeline se rige por Orden.
            p1.Notas.Add(new PendienteNota { HistId = 100, Orden = 0, Fecha = new DateOnly(2026, 7, 6), Nota = "primera", Autor = "Persona B" });
            p1.Notas.Add(new PendienteNota { HistId = 102, Orden = 1, Fecha = new DateOnly(2026, 7, 3), Nota = "segunda", Autor = "Persona B" });
            p1.Notas.Add(new PendienteNota { HistId = 101, Orden = 2, Fecha = new DateOnly(2026, 7, 20), Nota = "tercera", Autor = null });
            _rows.Add(p1);

            _rows.Add(new Row
            {
                Area = "CDC",
                Item = new PendienteItem
                {
                    Id = "p2", ClienteNum = 1, Descripcion = "Pendiente de ejemplo", Tipo = "PENDIENTE",
                    Prioridad = "MEDIA", Estado = "EN_PROGRESO", FechaCreacion = new DateOnly(2026, 6, 30),
                    Actualizado = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc),
                },
            });

            var p3 = new Row
            {
                Area = "CDC",
                Item = new PendienteItem
                {
                    Id = "p3", ClienteNum = 2, Descripcion = "Con hueco en Orden", Tipo = "PENDIENTE",
                    Prioridad = "BAJA", Estado = "ABIERTO", FechaCreacion = new DateOnly(2026, 7, 1),
                    Actualizado = new DateTime(2026, 7, 25, 9, 0, 0, DateTimeKind.Utc),
                },
            };
            p3.Notas.Add(new PendienteNota { HistId = 150, Orden = 7, Fecha = new DateOnly(2026, 7, 10), Nota = "unica", Autor = "Persona B" });
            _rows.Add(p3);

            _rows.Add(new Row
            {
                Area = "INFRA",
                Item = new PendienteItem
                {
                    Id = "in1", ClienteNum = 9, Descripcion = "Pendiente de infra", Tipo = "PENDIENTE",
                    Prioridad = "MEDIA", Estado = "ABIERTO", FechaCreacion = new DateOnly(2026, 6, 5),
                    Actualizado = new DateTime(2026, 7, 20, 8, 0, 0, DateTimeKind.Utc),
                },
            });
            return this;
        }

        private Row? Find(string area, string id) =>
            _rows.SingleOrDefault(r => r.Area == area && r.Item.Id == id);

        private static PendienteItem Compose(Row row) =>
            row.Item with { Historial = row.Notas.OrderBy(n => n.Orden).ThenBy(n => n.HistId).ToList() };

        public Task<PendientesPayload> GetAreaAsync(string area, CancellationToken ct = default) =>
            Task.FromResult(new PendientesPayload
            {
                Area = area,
                Clientes = _clientes.Where(c => c.Area == area).Select(c => c.Cliente)
                    .OrderBy(c => c.Num).ToList(),
                Pendientes = _rows.Where(r => r.Area == area).Select(Compose)
                    .OrderByDescending(p => p.Actualizado).ThenBy(p => p.Id).ToList(),
            });

        public Task<PendienteItem?> GetItemAsync(string area, string id, CancellationToken ct = default)
        {
            var row = Find(area, id);
            return Task.FromResult(row is null ? null : Compose(row));
        }

        public Task<string> CreateItemAsync(string area, PendienteWrite data, CancellationToken ct = default)
        {
            var id = $"pl-test-{++_idSeq}";
            _rows.Add(new Row
            {
                Area = area,
                Item = new PendienteItem
                {
                    Id = id, ClienteNum = data.ClienteNum, Titulo = Blank(data.Titulo),
                    Descripcion = Blank(data.Descripcion), Tipo = data.Tipo, Prioridad = data.Prioridad,
                    Estado = data.Estado, Responsable = Blank(data.Responsable),
                    FechaCreacion = DateOnly.FromDateTime(DateTime.UtcNow), Actualizado = DateTime.UtcNow,
                },
            });
            return Task.FromResult(id);
        }

        public Task<WriteOutcome> UpdateItemAsync(
            string area, string id, PendienteWrite data, CancellationToken ct = default)
        {
            var row = Find(area, id);
            if (row is null) return Task.FromResult(WriteOutcome.NotFound);
            if (data.Actualizado?.Ticks != row.Item.Actualizado.Ticks)
                return Task.FromResult(WriteOutcome.Conflict);

            row.Item = row.Item with
            {
                ClienteNum = data.ClienteNum,
                Titulo = Blank(data.Titulo),
                Descripcion = Blank(data.Descripcion),
                Tipo = data.Tipo,
                Prioridad = data.Prioridad,
                Estado = data.Estado,
                Responsable = Blank(data.Responsable),
                Actualizado = DateTime.UtcNow,
            };
            return Task.FromResult(WriteOutcome.Ok);
        }

        public Task<bool> DeleteItemAsync(string area, string id, CancellationToken ct = default)
        {
            var row = Find(area, id);
            if (row is null) return Task.FromResult(false);
            _rows.Remove(row); // las notas viven dentro de la fila: se van con ella
            return Task.FromResult(true);
        }

        public Task<int?> AddNotaAsync(
            string area, string id, NotaWrite data, string? autor, CancellationToken ct = default)
        {
            var row = Find(area, id);
            if (row is null) return Task.FromResult<int?>(null);

            var orden = row.Notas.Count == 0 ? 0 : row.Notas.Max(n => n.Orden) + 1;
            var histId = ++_histId;
            row.Notas.Add(new PendienteNota
            {
                HistId = histId,
                Orden = orden,
                Fecha = data.Fecha ?? DateOnly.FromDateTime(DateTime.UtcNow),
                Nota = data.Nota,
                Autor = Blank(autor),
            });
            row.Item = row.Item with { Actualizado = DateTime.UtcNow };
            return Task.FromResult<int?>(histId);
        }

        public Task<bool> DeleteNotaAsync(string area, string id, int histId, CancellationToken ct = default)
        {
            var row = Find(area, id);
            var nota = row?.Notas.SingleOrDefault(n => n.HistId == histId);
            if (row is null || nota is null) return Task.FromResult(false);
            row.Notas.Remove(nota);
            row.Item = row.Item with { Actualizado = DateTime.UtcNow };
            return Task.FromResult(true);
        }

        public Task<bool> ClienteExistsAsync(string area, int num, CancellationToken ct = default) =>
            Task.FromResult(_clientes.Any(c => c.Area == area && c.Cliente.Num == num));

        public Task<int> CreateClienteAsync(string area, ClienteWrite data, CancellationToken ct = default)
        {
            var num = _clientes.Where(c => c.Area == area).Select(c => c.Cliente.Num).DefaultIfEmpty(0).Max() + 1;
            _clientes.Add((area, new PendienteCliente
            {
                Num = num, Cliente = data.Cliente.Trim(), Servicio = Blank(data.Servicio),
                Categoria = Blank(data.Categoria), Pais = Blank(data.Pais),
                Coordinador = Blank(data.Coordinador), Consultor = Blank(data.Consultor),
            }));
            return Task.FromResult(num);
        }

        public Task<bool> UpdateClienteAsync(
            string area, int num, ClienteWrite data, CancellationToken ct = default)
        {
            var index = _clientes.FindIndex(c => c.Area == area && c.Cliente.Num == num);
            if (index < 0) return Task.FromResult(false);
            _clientes[index] = (area, _clientes[index].Cliente with
            {
                Cliente = data.Cliente.Trim(),
                Servicio = Blank(data.Servicio),
                Categoria = Blank(data.Categoria),
                Pais = Blank(data.Pais),
                Coordinador = Blank(data.Coordinador),
                Consultor = Blank(data.Consultor),
            });
            return Task.FromResult(true);
        }

        public Task<ClienteDeleteOutcome> DeleteClienteAsync(
            string area, int num, CancellationToken ct = default)
        {
            if (_rows.Any(r => r.Area == area && r.Item.ClienteNum == num))
                return Task.FromResult(ClienteDeleteOutcome.HasPendientes);

            var index = _clientes.FindIndex(c => c.Area == area && c.Cliente.Num == num);
            if (index < 0) return Task.FromResult(ClienteDeleteOutcome.NotFound);
            _clientes.RemoveAt(index);
            return Task.FromResult(ClienteDeleteOutcome.Ok);
        }

        private static string? Blank(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
