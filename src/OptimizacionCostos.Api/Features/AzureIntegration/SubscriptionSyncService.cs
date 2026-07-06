using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace OptimizacionCostos.Api.Features.AzureIntegration;

/// <summary>El cliente no tiene credenciales activas (→ 400).</summary>
public sealed class NoActiveCredentialsException() : Exception("No active credentials for this client");

public sealed record SyncError(int CredentialId, string? CredentialName, string Error);

public sealed record SubscriptionSyncSummary(
    int ClientId, int CredentialsChecked, int Created, int Updated, int Deactivated,
    int ActiveTotal, IReadOnlyList<SyncError> Errors);

/// <summary>
/// Sincroniza suscripciones Azure de un cliente. Port de sync_client_subscriptions /
/// fetch_azure_subscriptions de app/routes/azure_subscriptions.py. Descubre suscripciones por
/// cada credencial activa (ARM REST), hace upsert y desactiva las que ya no aparecen.
/// is_managed NUNCA se toca aquí (lo decide el usuario).
/// </summary>
public interface ISubscriptionSyncService
{
    Task<SubscriptionSyncSummary> SyncAsync(int clientId, CancellationToken ct = default);
}

public sealed class SubscriptionSyncService(
    IClientSubscriptionStore store,
    IAzureCredentialFactory factory,
    IHttpClientFactory httpFactory,
    ILogger<SubscriptionSyncService> logger) : ISubscriptionSyncService
{
    private const string ArmScope = "https://management.azure.com/.default";

    public async Task<SubscriptionSyncSummary> SyncAsync(int clientId, CancellationToken ct = default)
    {
        await store.EnsureSchemaAsync(ct);
        var credentials = await store.ActiveCredentialsAsync(clientId, ct);
        if (credentials.Count == 0)
            throw new NoActiveCredentialsException();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<SyncError>();
        int created = 0, updated = 0;

        foreach (var cred in credentials)
        {
            // Las credenciales de sesión de usuario no se sincronizan: sus suscripciones
            // se administran solo vía /azure/user-sessions/current/link (si no, el sync
            // metería TODO el portafolio Lighthouse del usuario a este cliente).
            if (string.Equals(cred.AuthType, "user_session", StringComparison.OrdinalIgnoreCase))
                continue;

            List<(string Id, string? Name)> subs;
            try
            {
                subs = await FetchSubscriptionsAsync(cred.CredentialId, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Subscription sync failed credential_id={Id} type={Type}", cred.CredentialId, ex.GetType().Name);
                var msg = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
                errors.Add(new SyncError(cred.CredentialId, cred.CredentialName, $"{ex.GetType().Name}: {msg}"));
                continue;
            }

            foreach (var (sid, name) in subs)
            {
                if (!seen.Add(sid)) continue;
                var result = await store.UpsertAsync(clientId, cred.CredentialId, sid, name, ct);
                if (result == "created") created++; else updated++;
            }
        }

        var deactivated = await store.DeactivateMissingAsync(clientId, seen, ct);
        return new SubscriptionSyncSummary(clientId, credentials.Count, created, updated, deactivated, seen.Count, errors);
    }

    // Port de fetch_azure_subscriptions: token ARM + GET /subscriptions (REST).
    private async Task<List<(string Id, string? Name)>> FetchSubscriptionsAsync(int credentialId, CancellationToken ct)
    {
        var credential = await factory.GetClientSecretCredentialAsync(credentialId, ct);
        var token = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);

        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            "https://management.azure.com/subscriptions?api-version=2020-01-01");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<(string, string?)>();
        if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var sid = item.TryGetProperty("subscriptionId", out var s) ? s.GetString() : null;
                if (string.IsNullOrEmpty(sid)) continue;
                var name = item.TryGetProperty("displayName", out var d) ? d.GetString() : null;
                list.Add((sid!, string.IsNullOrEmpty(name) ? sid : name));
            }
        }
        return list;
    }
}
