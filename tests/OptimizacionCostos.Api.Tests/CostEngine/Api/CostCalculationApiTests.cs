using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Tests.CostEngine.Api;

/// <summary>
/// Pruebas del router de costos (Fase 3). NO requieren Azure ni SQL (BD mockeada).
/// Cubren: 401 sin token, control de acceso por cliente (403 / 200), 404 de análisis
/// inexistente, costo manual (update + 404) y el gate de admin de prices/refresh-all.
/// </summary>
public class CostCalculationApiTests : IClassFixture<CostApiTestFactory>
{
    private readonly CostApiTestFactory _factory;

    public CostCalculationApiTests(CostApiTestFactory factory) => _factory = factory;

    private HttpClient ClientFor(string? email, string? role)
    {
        var client = _factory.CreateClient();
        if (email is not null && role is not null)
        {
            _factory.Directory.Add(email, role);
            var token = BitJwt.Create(CostApiTestFactory.Secret, email, "Test User", role);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    // ---- 401 sin token ----

    [Fact]
    public async Task Sin_token_devuelve_401()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/analysis/1/results");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Sin_token_calculate_devuelve_401()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsync("/analysis/1/calculate", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- Control de acceso por cliente ----

    [Fact]
    public async Task Consultor_sin_cliente_asignado_devuelve_403()
    {
        // analysis 10 pertenece al cliente 5; el consultor no tiene asignaciones.
        _factory.Access.AnalysisToClient[10] = 5;
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);

        var res = await client.GetAsync("/analysis/10/results");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Lector_sin_cliente_asignado_devuelve_403()
    {
        _factory.Access.AnalysisToClient[11] = 7;
        var client = ClientFor("lector@bit.ec", Roles.Lector);

        var res = await client.GetAsync("/analysis/11/scenarios");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Consultor_con_cliente_asignado_accede_200()
    {
        _factory.Access.AnalysisToClient[12] = 8;
        _factory.Access.Assignments["asignado@bit.ec"] = new HashSet<int> { 8 };
        _factory.Query.ResultsByAnalysis[12] = new()
        {
            new CostResultRow { CostResultId = 1, ResourceId = 100, ServiceKey = "vms", PaygMonthly = 12.5 },
        };
        var client = ClientFor("asignado@bit.ec", Roles.Consultor);

        var res = await client.GetAsync("/analysis/12/results");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal("vms", body[0].GetProperty("service_key").GetString());
    }

    [Fact]
    public async Task Admin_accede_a_cualquier_cliente_200()
    {
        _factory.Access.AnalysisToClient[13] = 99; // admin no necesita asignación.
        var client = ClientFor("admin@bit.ec", Roles.Admin);

        var res = await client.GetAsync("/analysis/13/results");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ---- 404 análisis inexistente ----

    [Fact]
    public async Task Analisis_inexistente_devuelve_404()
    {
        // 777 no está en el mapa -> NotFound, incluso para admin.
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var res = await client.GetAsync("/analysis/777/results");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ---- Contrato snake_case de /results ----

    [Fact]
    public async Task Results_salen_en_snake_case()
    {
        _factory.Access.AnalysisToClient[14] = 1;
        _factory.Query.ResultsByAnalysis[14] = new()
        {
            new CostResultRow
            {
                CostResultId = 9, ResourceId = 200, ServiceKey = "disks",
                PaygMonthly = 5.0, RiCoverage = "confirmed", RiReservationName = "res-1", RiTerm = "P1Y",
                PowerRunningHours = 720, PowerUptimePct = 100,
            },
        };
        var client = ClientFor("admin@bit.ec", Roles.Admin);

        var items = await client.GetFromJsonAsync<JsonElement>("/analysis/14/results");
        var first = items[0];
        Assert.True(first.TryGetProperty("cost_result_id", out _));
        Assert.True(first.TryGetProperty("payg_monthly", out _));
        Assert.True(first.TryGetProperty("ri_coverage", out _));
        Assert.True(first.TryGetProperty("ri_reservation_name", out _));
        Assert.True(first.TryGetProperty("ri_term", out _));
        Assert.True(first.TryGetProperty("power_running_hours", out _));
        Assert.True(first.TryGetProperty("power_uptime_pct", out _));
    }

    // ---- manual-cost: update + 404 ----

    [Fact]
    public async Task Manual_cost_actualiza_200()
    {
        _factory.Access.AnalysisToClient[15] = 1;
        _factory.Access.CostResultToAnalysis[500] = 15;
        _factory.Query.ExistingCostResults.Add(500);
        var client = ClientFor("admin@bit.ec", Roles.Admin);

        var res = await client.PutAsJsonAsync(
            "/cost-results/500/manual-cost", new { manual_monthly_cost = 42.0, manual_cost_note = "ajuste" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(500, body.GetProperty("cost_result_id").GetInt32());
        Assert.Contains((42.0, "ajuste"),
            _factory.Query.SetManualCostCalls.Select(c => (c.Cost ?? -1, c.Note ?? "")));
    }

    [Fact]
    public async Task Manual_cost_inexistente_devuelve_404()
    {
        // El cost_result existe en el mapa de acceso (para pasar el assert) pero no en la BD.
        _factory.Access.AnalysisToClient[16] = 1;
        _factory.Access.CostResultToAnalysis[600] = 16;
        // NO se agrega a ExistingCostResults -> el UPDATE no afecta filas -> 404.
        var client = ClientFor("admin@bit.ec", Roles.Admin);

        var res = await client.PutAsJsonAsync(
            "/cost-results/600/manual-cost", new { manual_monthly_cost = 1.0 });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Manual_cost_de_cost_result_inexistente_en_acceso_devuelve_404()
    {
        // 999 no está en CostResultToAnalysis -> el control de acceso devuelve 404.
        var client = ClientFor("admin@bit.ec", Roles.Admin);
        var res = await client.PutAsJsonAsync(
            "/cost-results/999/manual-cost", new { manual_monthly_cost = 1.0 });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Manual_cost_consultor_sin_cliente_devuelve_403()
    {
        _factory.Access.AnalysisToClient[17] = 33;
        _factory.Access.CostResultToAnalysis[700] = 17;
        _factory.Query.ExistingCostResults.Add(700);
        var client = ClientFor("otro@bit.ec", Roles.Consultor);

        var res = await client.PutAsJsonAsync(
            "/cost-results/700/manual-cost", new { manual_monthly_cost = 1.0 });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- prices/refresh-all: solo admin ----

    [Fact]
    public async Task Refresh_all_consultor_devuelve_403()
    {
        var client = ClientFor("consultor@bit.ec", Roles.Consultor);
        var res = await client.PostAsync("/prices/refresh-all", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Refresh_all_lector_devuelve_403()
    {
        var client = ClientFor("lector@bit.ec", Roles.Lector);
        var res = await client.PostAsync("/prices/refresh-all", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Refresh_all_admin_borra_cache_200()
    {
        _factory.PriceCache.ClearAllReturns = 123;
        var client = ClientFor("admin@bit.ec", Roles.Admin);

        var res = await client.PostAsync("/prices/refresh-all", content: null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(123, body.GetProperty("removed_rows").GetInt32());
    }

    // ---- cache-status: cualquier autenticado ----

    [Fact]
    public async Task Cache_status_lector_200_snake_case()
    {
        _factory.PriceCache.StatusReturns = new List<PriceCacheStatusRow>
        {
            new() { ServiceName = "Virtual Machines", Region = "eastus", PricesCount = 3, IsFresh = true },
        };
        var client = ClientFor("lector@bit.ec", Roles.Lector);

        var items = await client.GetFromJsonAsync<JsonElement>("/prices/cache-status");
        Assert.Equal(1, items.GetArrayLength());
        var first = items[0];
        Assert.True(first.TryGetProperty("service_name", out _));
        Assert.True(first.TryGetProperty("prices_count", out _));
        Assert.True(first.TryGetProperty("is_fresh", out _));
    }

    // ---- calculate: acceso ok devuelve summary + scenarios_error (sin cost_results) ----

    [Fact]
    public async Task Calculate_admin_devuelve_summary()
    {
        _factory.Access.AnalysisToClient[18] = 1;
        var client = ClientFor("admin@bit.ec", Roles.Admin);

        var res = await client.PostAsJsonAsync(
            "/analysis/18/calculate", new { auto_build_scenarios = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(18, body.GetProperty("analysis_id").GetInt32());
    }

    [Fact]
    public async Task Calculate_resource_limit_invalido_devuelve_400()
    {
        _factory.Access.AnalysisToClient[19] = 1;
        var client = ClientFor("admin@bit.ec", Roles.Admin);

        var res = await client.PostAsJsonAsync(
            "/analysis/19/calculate", new { resource_limit = 999 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
