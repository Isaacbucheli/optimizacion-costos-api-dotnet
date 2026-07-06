using OptimizacionCostos.Api.Features.AzureIntegration;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace OptimizacionCostos.Api.Tests.AzureIntegration.UserSessions;

/// <summary>Store fake: credenciales configurables; registra upserts/desactivaciones.</summary>
public sealed class FakeSubscriptionStore : IClientSubscriptionStore
{
    public List<ActiveCredential> Credentials { get; } = [];
    public List<(int CredentialId, string SubId)> Upserts { get; } = [];
    public bool DeactivateCalled { get; private set; }

    public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ActiveCredential>> ActiveCredentialsAsync(int clientId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ActiveCredential>>(Credentials);
    public Task<string> UpsertAsync(int clientId, int credentialId, string subscriptionId, string? subscriptionName, CancellationToken ct = default)
    { Upserts.Add((credentialId, subscriptionId)); return Task.FromResult("created"); }
    public Task<int> DeactivateMissingAsync(int clientId, IReadOnlyCollection<string> keep, CancellationToken ct = default)
    { DeactivateCalled = true; return Task.FromResult(0); }
    public Task<IReadOnlyList<SubscriptionListItem>> ListByClientAsync(int clientId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SubscriptionListItem>>([]);
    public Task<int?> ClientIdForCredentialAsync(int credentialId, CancellationToken ct = default) => Task.FromResult<int?>(null);
    public Task<int?> ClientIdForSubscriptionAsync(int id, CancellationToken ct = default) => Task.FromResult<int?>(null);
    public Task<int> InsertManualAsync(int clientId, int credentialId, string subscriptionId, string? name, CancellationToken ct = default)
        => Task.FromResult(1);
    public Task<bool> UpdateAsync(int id, string? name, bool? isActive, bool? isManaged, CancellationToken ct = default)
        => Task.FromResult(true);
}

/// <summary>Factory fake: registra qué credential_id se piden.</summary>
public sealed class FakeCredentialFactory : IAzureCredentialFactory
{
    public List<int> Requested { get; } = [];
    public Task<TokenCredential> GetClientSecretCredentialAsync(int credentialId, CancellationToken ct = default)
    {
        Requested.Add(credentialId);
        return Task.FromResult<TokenCredential>(new FakeTokenCredential(
            new AccessToken("t", DateTimeOffset.UtcNow.AddHours(1))));
    }
    public Task<CredentialAuthResult> TestCredentialAuthAsync(int credentialId, CancellationToken ct = default)
        => Task.FromResult(new CredentialAuthResult(true, null, null));
    public Task UpdateValidationStatusAsync(int credentialId, bool success, string? error = null, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task WriteAuditLogAsync(int credentialId, string action, string? actor = null, string? details = null, CancellationToken ct = default)
        => Task.CompletedTask;
}

public sealed class FakeHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

public sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(respond(request));
}

public class SubscriptionSyncSkipTests
{
    private static HttpResponseMessage SubsJson() => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""{"value":[{"subscriptionId":"sub-1","displayName":"Sub Uno"}]}"""),
    };

    [Fact]
    public async Task Sync_salta_credenciales_user_session_y_procesa_app_secret()
    {
        var store = new FakeSubscriptionStore();
        store.Credentials.Add(new ActiveCredential(1, "sp", "app_secret"));
        store.Credentials.Add(new ActiveCredential(2, "sesion", "user_session"));
        var factory = new FakeCredentialFactory();
        var svc = new SubscriptionSyncService(store, factory,
            new FakeHttpFactory(new CannedHandler(_ => SubsJson())), NullLogger<SubscriptionSyncService>.Instance);

        var summary = await svc.SyncAsync(clientId: 9);

        Assert.Equal([1], factory.Requested);              // nunca pidió la credencial 2
        Assert.Equal([(1, "sub-1")], store.Upserts);
        Assert.Equal(2, summary.CredentialsChecked);
    }

    [Fact]
    public async Task Sync_con_solo_user_session_no_llama_azure_ni_lanza()
    {
        var store = new FakeSubscriptionStore();
        store.Credentials.Add(new ActiveCredential(2, "sesion", "user_session"));
        var factory = new FakeCredentialFactory();
        var svc = new SubscriptionSyncService(store, factory,
            new FakeHttpFactory(new CannedHandler(_ => SubsJson())), NullLogger<SubscriptionSyncService>.Instance);

        var summary = await svc.SyncAsync(clientId: 9);

        Assert.Empty(factory.Requested);
        Assert.Empty(store.Upserts);
        Assert.Equal(0, summary.Created);
    }
}
