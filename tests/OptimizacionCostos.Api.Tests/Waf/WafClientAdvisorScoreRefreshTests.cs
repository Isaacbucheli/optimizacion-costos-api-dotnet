using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.Waf;
using OptimizacionCostos.Api.Features.Waf.Api;
using OptimizacionCostos.Api.Tests.CostEngine.Api;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// POST /waf/clients/{id}/advisor-score/refresh — hermano por-cliente del refresh admin, para que
/// un consultor con "Editar" en Recomendaciones pueda actualizar el score de SUS clientes.
/// Lo que se blinda aquí es justo lo que hace peligroso abrir el rol: el módulo debe exigir Edit,
/// y el cliente debe pasar por el control de acceso (nada de refrescar clientes ajenos).
/// El camino feliz toca SQL en GuardAsync, así que se verifica en E2E, no aquí.
/// </summary>
public sealed class WafClientAdvisorScoreRefreshTests : IClassFixture<WafClientAdvisorScoreRefreshTests.Factory>
{
    private const int AssignedClient = 7;
    private const int ForeignClient = 99;

    private readonly Factory _factory;
    public WafClientAdvisorScoreRefreshTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string email, string role)
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        var token = BitJwt.Create(Factory.Secret, email, "Test User", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string Url(int clientId) => $"/waf/clients/{clientId}/advisor-score/refresh";

    [Fact]
    public async Task SinToken_401()
    {
        var res = await _factory.CreateClient().PostAsync(Url(AssignedClient), Json("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Consultor_sin_Editar_en_el_modulo_recibe_403()
    {
        _factory.Perms.Set(Roles.Consultor, Modules.Waf, canView: true, canEdit: false);
        _factory.PermService.Invalidate();
        _factory.Access.Assignments["ro@bit.ec"] = [AssignedClient];

        var res = await ClientFor("ro@bit.ec", Roles.Consultor).PostAsync(Url(AssignedClient), Json("{}"));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Empty(_factory.Advisor.RefreshClientCalls);
    }

    [Fact]
    public async Task Lector_no_refresca_aunque_la_matriz_diga_que_si()
    {
        _factory.Perms.Set(Roles.Lector, Modules.Waf, canView: true, canEdit: true); // candado duro del servicio
        _factory.PermService.Invalidate();
        _factory.Access.Assignments["l@bit.ec"] = [AssignedClient];

        var res = await ClientFor("l@bit.ec", Roles.Lector).PostAsync(Url(AssignedClient), Json("{}"));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Empty(_factory.Advisor.RefreshClientCalls);
    }

    // El riesgo real de abrir el rol: que un consultor refresque clientes que no le asignaron.
    [Fact]
    public async Task Consultor_con_Editar_no_refresca_un_cliente_que_no_tiene_asignado()
    {
        _factory.Perms.Set(Roles.Consultor, Modules.Waf, canView: true, canEdit: true);
        _factory.PermService.Invalidate();
        _factory.Access.Assignments["c@bit.ec"] = [AssignedClient];

        var res = await ClientFor("c@bit.ec", Roles.Consultor).PostAsync(Url(ForeignClient), Json("{}"));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Empty(_factory.Advisor.RefreshClientCalls);
    }

    [Fact]
    public void El_endpoint_exige_Editar_del_modulo_waf_y_cuelga_de_la_ruta_por_cliente()
    {
        var method = typeof(WafController).GetMethod(
            nameof(WafController.RefreshClientAdvisorScore),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotNull(method);

        var gate = method!.GetCustomAttribute<RequireModuleAttribute>();
        Assert.NotNull(gate);
        Assert.Equal(Modules.Waf, gate!.ModuleKey);
        Assert.Equal(ModuleAccess.Edit, gate.Access);

        var route = method.GetCustomAttribute<HttpPostAttribute>();
        Assert.Equal("clients/{clientId:int}/advisor-score/refresh", route?.Template);
    }

    // El body no lleva client_id a propósito: el cliente sale de la ruta (ya validada por acceso).
    [Fact]
    public void El_request_por_cliente_no_acepta_client_id_en_el_body()
    {
        var props = typeof(WafClientAdvisorScoreRefreshRequest).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains(nameof(WafClientAdvisorScoreRefreshRequest.IncludeInReports), props);
        Assert.DoesNotContain("ClientId", props);
    }

    // ---- Fixture: pipeline MVC real; se fake-an auth, permisos, acceso por cliente y el servicio ----
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeAnalysisAccess Access { get; } = new();
        public FakeAdvisorScoreRefresher Advisor { get; } = new();
        public FakeModulePermissionStore Perms { get; } = new FakeModulePermissionStore().SeedDefaults();
        public IModulePermissionService PermService => Services.GetRequiredService<IModulePermissionService>();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IAnalysisAccess>();
                services.AddSingleton<IAnalysisAccess>(Access);
                services.RemoveAll<IAdvisorScoreService>();
                services.AddSingleton<IAdvisorScoreService>(Advisor);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(Perms);
                // Singleton en tests para poder invalidar la caché fuera del scope del request.
                services.RemoveAll<IModulePermissionService>();
                services.AddSingleton<IModulePermissionService, ModulePermissionService>();
            });
        }
    }

    /// <summary>Fake que solo registra los refresh por cliente (el resto no participa en estos tests).</summary>
    public sealed class FakeAdvisorScoreRefresher : IAdvisorScoreService
    {
        public List<int> RefreshClientCalls { get; } = [];

        public Task<WafAdvisorScoreSnapshot?> RefreshClientAsync(
            int clientId, DateOnly? snapshotDate, string source, bool includeInReports, CancellationToken ct = default)
        {
            RefreshClientCalls.Add(clientId);
            return Task.FromResult<WafAdvisorScoreSnapshot?>(null);
        }

        public Task<WafRefreshAllResult> RefreshAllAsync(
            IReadOnlyList<int>? clientIds, string source, bool includeInReports, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RefreshClientScoreHistoryAsync(int clientId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<int, AdvisorScoreData>> FetchSubscriptionScoreAsync(
            int credentialId, string subscriptionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<WafAdvisorScoreResult> ComputeClientScoreAsync(
            int clientId, bool includeBreakdown, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(IReadOnlyList<AdvisorRow> Rows, WafIngestionMetrics Metrics)> GenerateAndListRecommendationsAsync(
            int credentialId, string subscriptionId, string subscriptionName,
            int timeoutSeconds = 600, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlySet<string>?> FetchApplicableAssessmentTypesAsync(
            int credentialId, string subscriptionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>?>(null);
    }
}
