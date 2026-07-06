using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.AzureIntegration;
using OptimizacionCostos.Api.Features.AzureIntegration.UserSessions;
using OptimizacionCostos.Api.Features.Clients;

namespace OptimizacionCostos.Api.Tests.AzureIntegration.UserSessions;

/// <summary>ClientStore fake mínimo: crea con ids incrementales.</summary>
public sealed class FakeClientStore : IClientStore
{
    public List<(int Id, string Name, string? Notes)> Created { get; } = [];
    private int _seq = 100;
    public Task<int> CreateAsync(string clientName, string? taxId, string? contactName, string? contactEmail, string? notes, CancellationToken ct = default)
    { var id = ++_seq; Created.Add((id, clientName, notes)); return Task.FromResult(id); }
    public Task<bool> NameExistsAsync(string name, int excludeClientId, CancellationToken ct = default)
        => Task.FromResult(Created.Any(c => c.Name == name));

    // Resto de IClientStore: no usado por estos tests.
    public Task<IReadOnlyList<ClientListItem>> ListAsync(CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> ExistsAsync(int clientId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> RenameAsync(int clientId, string name, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<string?> GetNameAsync(int clientId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<(string Name, string? LogoBlobName)?> GetNameAndLogoAsync(int clientId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<IReadOnlyDictionary<string, int>> PurgeDataAsync(int clientId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<(IReadOnlyDictionary<string, int> Counts, string? LogoBlobName)> DeleteClientCascadeAsync(int clientId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task UpdateLogoMetaAsync(int clientId, string blobName, string contentType, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<(string? BlobName, string? ContentType)?> GetLogoAsync(int clientId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task ClearLogoAsync(int clientId, CancellationToken ct = default)
        => throw new NotImplementedException();
}

/// <summary>CredentialStore fake: registra el insert de sesión.</summary>
public sealed class FakeCredentialStore : IClientCredentialStore
{
    public List<(int ClientId, string Name, string Tenant, string AppId, string Email)> SessionInserts { get; } = [];
    public Task<int> InsertUserSessionAsync(int clientId, string name, string tenantId, string appClientId, string sessionUserEmail, CancellationToken ct = default)
    { SessionInserts.Add((clientId, name, tenantId, appClientId, sessionUserEmail)); return Task.FromResult(555); }

    // Resto de IClientCredentialStore: no usado por estos tests.
    public Task<IReadOnlyList<CredentialListItem>> ListByClientAsync(int clientId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<int> InsertAsync(int clientId, string name, string tenantId, string appClientId, string secretName, DateTime? secretExpiresAt, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<string?> GetSecretNameAsync(int credentialId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task UpdateRotateMetaAsync(int credentialId, DateTime? secretExpiresAt, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<bool> UpdateMetaAsync(int credentialId, string? name, bool? isActive, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<int?> ClientIdForAsync(int credentialId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<IReadOnlyList<CredentialAuditItem>> GetAuditAsync(int credentialId, int limit, CancellationToken ct = default)
        => throw new NotImplementedException();
}

[Collection("UserSessionsEnv")]
public class UserSessionLinkApiTests
{
    private sealed class Factory : UserSessionsApiTestFactory
    {
        public FakeClientStore Clients { get; } = new();
        public FakeCredentialStore Creds { get; } = new();
        public FakeSubscriptionStore Subs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClientStore>();
                services.AddSingleton<IClientStore>(Clients);
                services.RemoveAll<IClientCredentialStore>();
                services.AddSingleton<IClientCredentialStore>(Creds);
                services.RemoveAll<IClientSubscriptionStore>();
                services.AddSingleton<IClientSubscriptionStore>(Subs);
                services.RemoveAll<IAzureCredentialFactory>();
                services.AddSingleton<IAzureCredentialFactory>(new FakeCredentialFactory());
            });
        }
    }

    private static async Task<HttpClient> AuthedWithSession(Factory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BitJwt.Create(UserSessionsApiTestFactory.Secret, "admin@bit.ec", "Test User", Roles.Admin));
        await c.PostAsync("/azure/user-sessions", null);
        f.Flow.Tcs.SetResult(new DeviceFlowResult(
            new FakeTokenCredential(new Azure.Core.AccessToken("t", DateTimeOffset.UtcNow.AddHours(1))),
            new Azure.Core.AccessToken("t", DateTimeOffset.UtcNow.AddHours(1)), "isaac@grupobusiness.it"));
        // Esperar autenticación (el fake resuelve casi instantáneo).
        for (var i = 0; i < 100; i++)
        {
            var s = JsonNode.Parse(await c.GetStringAsync("/azure/user-sessions/current"))!;
            if (s["status"]!.GetValue<string>() == "authenticated") break;
            await Task.Delay(10);
        }
        return c;
    }

    [Fact]
    public async Task Link_con_cliente_nuevo_crea_cliente_credencial_y_suscripciones()
    {
        using var f = new Factory();
        var client = await AuthedWithSession(f);

        var res = await client.PostAsJsonAsync("/azure/user-sessions/current/link", new
        {
            new_client_name = "SG Investment (temporal)",
            tenant_id = "tenant-cliente-a",
            subscriptions = new[] { new { subscription_id = "s1", display_name = "Microsoft Azure" } },
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal(101, body["client_id"]!.GetValue<int>());
        Assert.Equal(555, body["credential_id"]!.GetValue<int>());
        Assert.Equal(1, body["subscriptions_linked"]!.GetValue<int>());

        var ins = Assert.Single(f.Creds.SessionInserts);
        Assert.Equal((101, "Sesión de admin@bit.ec", "tenant-cliente-a",
            "04b07795-8ddb-461a-bbee-02f9e1bf7b46", "admin@bit.ec"), ins);
        Assert.Equal([(555, "s1")], f.Subs.Upserts);
    }

    [Fact]
    public async Task Link_sin_sesion_autenticada_devuelve_409()
    {
        using var f = new Factory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BitJwt.Create(UserSessionsApiTestFactory.Secret, "admin@bit.ec", "Test User", Roles.Admin));
        var res = await c.PostAsJsonAsync("/azure/user-sessions/current/link", new
        {
            new_client_name = "X", tenant_id = "t", subscriptions = new[] { new { subscription_id = "s1" } },
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Theory]
    [InlineData("""{"tenant_id":"t","subscriptions":[{"subscription_id":"s1"}]}""")]                                  // sin destino
    [InlineData("""{"client_id":1,"new_client_name":"X","tenant_id":"t","subscriptions":[{"subscription_id":"s1"}]}""")] // ambos destinos
    [InlineData("""{"new_client_name":"X","tenant_id":"t","subscriptions":[]}""")]                                     // sin subs
    [InlineData("""{"new_client_name":"X","subscriptions":[{"subscription_id":"s1"}]}""")]                             // sin tenant
    public async Task Link_payload_invalido_devuelve_400(string json)
    {
        using var f = new Factory();
        var client = await AuthedWithSession(f);
        var res = await client.PostAsync("/azure/user-sessions/current/link",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
