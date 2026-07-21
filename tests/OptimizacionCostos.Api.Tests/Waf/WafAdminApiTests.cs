using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.Waf;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Regresión del bug 2026-07-02: los records de request WAF tenían la validación DataAnnotations
/// con target [property: ...] sobre parámetros del constructor primario, lo que hace que ASP.NET
/// Core 8 lance InvalidOperationException DURANTE EL MODEL-BINDING (antes del try/catch del
/// controller) en cada POST con body → conexión cortada → "Failed to fetch" en el navegador.
/// Ningún test previo golpeaba /waf/admin/* por el pipeline, por eso pasó desapercibido.
/// Este test SÍ pasa por el pipeline MVC real (model binding + validación), que es donde vivía el bug.
/// </summary>
public sealed class WafAdminApiTests : IClassFixture<WafAdminApiTests.Factory>
{
    private readonly Factory _factory;
    public WafAdminApiTests(Factory factory) => _factory = factory;

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

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task AdvisorScoreRefresh_SinToken_401()
    {
        var res = await _factory.CreateClient().PostAsync("/waf/admin/advisor-score/refresh", Json("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task AdvisorScoreRefresh_Consultor_403()
    {
        var res = await ClientFor("c@bit.ec", Roles.Consultor).PostAsync("/waf/admin/advisor-score/refresh", Json("{}"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ESTE es el guard del bug: con el body real, el model-binding del record NO debe lanzar.
    [Fact]
    public async Task AdvisorScoreRefresh_Admin_ConBody_NoRompeElModelBinding()
    {
        var res = await ClientFor("a@bit.ec", Roles.Admin)
            .PostAsync("/waf/admin/advisor-score/refresh", Json("{\"include_in_reports\":true}"));

        // Antes del fix: InvalidOperationException en el model-binding → 500/conexión rota.
        // Con el fix: model binding OK → la acción corre → RefreshAllAsync (fake) → 200.
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("clients_total", body);
        Assert.True(_factory.Advisor.RefreshAllCalls >= 1);
    }

    // ---- Fixture: API real en memoria, solo se fake-an auth y el servicio de Advisor Score ----
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeAdvisorScoreService Advisor { get; } = new();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IAdvisorScoreService>();
                services.AddSingleton<IAdvisorScoreService>(Advisor);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
            });
        }
    }

    /// <summary>Fake determinista: solo la ruta all-clients importa para este test.</summary>
    public sealed class FakeAdvisorScoreService : IAdvisorScoreService
    {
        public int RefreshAllCalls { get; private set; }

        public Task<WafRefreshAllResult> RefreshAllAsync(
            IReadOnlyList<int>? clientIds, string source, bool includeInReports, CancellationToken ct = default)
        {
            RefreshAllCalls++;
            return Task.FromResult(new WafRefreshAllResult(0, 0, 0, Array.Empty<WafRefreshClientResult>()));
        }

        public Task<WafAdvisorScoreSnapshot?> RefreshClientAsync(
            int clientId, DateOnly? snapshotDate, string source, bool includeInReports, CancellationToken ct = default)
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
            => Task.FromResult<IReadOnlySet<string>?>(null); // fail-open: no filtra
    }
}
