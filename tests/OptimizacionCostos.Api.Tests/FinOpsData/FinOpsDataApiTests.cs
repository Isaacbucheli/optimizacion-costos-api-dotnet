using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using OptimizacionCostos.Api.Auth;
using Xunit;

namespace OptimizacionCostos.Api.Tests.FinOpsData;

/// <summary>
/// Pruebas del router /finops-data: refresh (admin), status y lookups (Fase 1 FinOps Toolkit).
/// BD/HTTP mockeados vía fakes en <see cref="FinOpsApiTestFactory"/>.
/// </summary>
public sealed class FinOpsDataApiTests : IClassFixture<FinOpsApiTestFactory>
{
    private readonly FinOpsApiTestFactory _factory;
    public FinOpsDataApiTests(FinOpsApiTestFactory factory) => _factory = factory;

    private HttpClient ClientFor(string? email, string? role)
    {
        var client = _factory.CreateClient();
        if (email is not null && role is not null)
        {
            _factory.Directory.Add(email, role);
            var token = BitJwt.Create(FinOpsApiTestFactory.Secret, email, "Test User", role);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    [Fact]
    public async Task Refresh_SinToken_401()
    {
        var res = await _factory.CreateClient().PostAsync("/finops-data/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Refresh_Consultor_403()
    {
        var res = await ClientFor("c@bit.ec", Roles.Consultor).PostAsync("/finops-data/refresh", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Refresh_Admin_200ConResumen()
    {
        var res = await ClientFor("a@bit.ec", Roles.Admin).PostAsync("/finops-data/refresh", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"results\"", body);
        Assert.Contains("\"commitment_eligibility\"", body);
    }

    [Fact]
    public async Task Status_Autenticado_200()
    {
        var res = await ClientFor("l@bit.ec", Roles.Lector).GetAsync("/finops-data/status");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Lookups_DevuelveMapas()
    {
        var res = await ClientFor("l@bit.ec", Roles.Lector).GetAsync("/finops-data/lookups");
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"regions\"", body);
        Assert.Contains("\"resource_types\"", body);
        Assert.Contains("\"service_categories\"", body);
        Assert.Contains("\"vms\":\"Compute\"", body);
    }

    [Fact]
    public async Task Refresh_Admin_InvalidaCacheDeLookups()
    {
        // Pre-seed: la caché real (registrada por Program.cs) tiene "finops:regions" fresca.
        var cache = _factory.Services.GetRequiredService<IMemoryCache>();
        cache.Set("finops:regions", new Dictionary<string, string> { ["stale"] = "Stale Region" });
        Assert.True(cache.TryGetValue("finops:regions", out _));

        var res = await ClientFor("a@bit.ec", Roles.Admin).PostAsync("/finops-data/refresh", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.False(cache.TryGetValue("finops:regions", out _));
    }
}
