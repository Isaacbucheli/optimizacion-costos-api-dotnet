using System.Text;
using System.Text.Json;
using Azure.Core;
using OptimizacionCostos.Api.Features.AzureIntegration;

namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

public sealed record GraphUser(string Id, string? DisplayName, string? Upn, string? Mail, string? UserType,
    bool? AccountEnabled, DateTimeOffset? CreatedAt, string? ExternalState, DateTimeOffset? LastSignIn);
public sealed record GraphUserSweep(IReadOnlyDictionary<string, GraphUser> ById, bool SignInActivityAvailable);
public sealed record GraphDirectoryObject(string Id, string? OdataType, string? DisplayName, string? AppId, string? Upn, string? UserType);

public interface IAccessReviewGraphClient
{
    Task<GraphUserSweep> SweepUsersAsync(int credentialId, CancellationToken ct = default);
    Task<IReadOnlyList<GraphDirectoryObject>> GetGroupTransitiveMembersAsync(int credentialId, string groupId, CancellationToken ct = default);
    Task<IReadOnlyList<GraphDirectoryObject>> GetGlobalAdminsAsync(int credentialId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, GraphDirectoryObject>> GetByIdsAsync(int credentialId, IReadOnlyCollection<string> ids, CancellationToken ct = default);
    /// <summary>enabled|disabled|unavailable.</summary>
    Task<string> GetMfaStatusAsync(int credentialId, string userId, CancellationToken ct = default);
}

/// <summary>Cliente Microsoft Graph para revisión de accesos. Token por credencial con cache
/// (evita cientos de client_credentials en el barrido de MFA).</summary>
public sealed class AccessReviewGraphClient(IAzureCredentialFactory credentials, IHttpClientFactory httpFactory)
    : IAccessReviewGraphClient
{
    private const string GraphScope = "https://graph.microsoft.com/.default";
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const int MaxPages = 100;
    private const string GaRoleDefinitionId = "62e90394-69f5-4237-9190-012177145e10";

    private readonly Dictionary<int, (string Token, DateTimeOffset ExpiresOn)> _tokenCache = [];
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private async Task<(HttpClient Http, string Token)> ClientAsync(int credentialId, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!_tokenCache.TryGetValue(credentialId, out var cached) ||
                cached.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                var cred = await credentials.GetClientSecretCredentialAsync(credentialId, ct);
                var token = await cred.GetTokenAsync(new TokenRequestContext([GraphScope]), ct);
                cached = (token.Token, token.ExpiresOn);
                _tokenCache[credentialId] = cached;
            }
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(60);
            return (http, cached.Token);
        }
        finally { _tokenLock.Release(); }
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient http, string token, string url,
        bool eventualConsistency, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        if (eventualConsistency) req.Headers.TryAddWithoutValidation("ConsistencyLevel", "eventual");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }

    /// <summary>GET paginado por @odata.nextLink acumulando "value".</summary>
    private static async Task<List<JsonElement>> GetPagedAsync(HttpClient http, string token, string url,
        bool eventualConsistency, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        string? next = url;
        var page = 0;
        while (next is not null && page++ < MaxPages)
        {
            var root = await GetJsonAsync(http, token, next, eventualConsistency, ct);
            if (root.TryGetProperty("value", out var value))
                foreach (var el in value.EnumerateArray()) items.Add(el.Clone());
            next = root.TryGetProperty("@odata.nextLink", out var nl) && nl.ValueKind == JsonValueKind.String
                ? nl.GetString() : null;
        }
        return items;
    }

    private static string? S(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool? B(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False) ? v.GetBoolean() : null;
    private static DateTimeOffset? Dt(string? s) =>
        DateTimeOffset.TryParse(s, out var d) ? d : null;

    private static GraphUser ParseUser(JsonElement el)
    {
        DateTimeOffset? lastSignIn = null;
        if (el.TryGetProperty("signInActivity", out var sia) && sia.ValueKind == JsonValueKind.Object)
            lastSignIn = Dt(S(sia, "lastSignInDateTime"));
        return new GraphUser(S(el, "id")!, S(el, "displayName"), S(el, "userPrincipalName"), S(el, "mail"),
            S(el, "userType"), B(el, "accountEnabled"), Dt(S(el, "createdDateTime")),
            S(el, "externalUserState"), lastSignIn);
    }

    public async Task<GraphUserSweep> SweepUsersAsync(int credentialId, CancellationToken ct = default)
    {
        var (http, token) = await ClientAsync(credentialId, ct);
        const string baseSelect = "id,displayName,userPrincipalName,mail,userType,accountEnabled,createdDateTime,externalUserState";
        var withSia = $"{GraphBase}/users?$select={baseSelect},signInActivity&$top=999";
        try
        {
            var items = await GetPagedAsync(http, token, withSia, eventualConsistency: false, ct);
            return new GraphUserSweep(items.Select(ParseUser).ToDictionary(u => u.Id), SignInActivityAvailable: true);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.BadRequest)
        {
            // Sin licencia Entra ID P1/P2 (o sin AuditLog.Read.All): reintento sin signInActivity.
            var items = await GetPagedAsync(http, token, $"{GraphBase}/users?$select={baseSelect}&$top=999",
                eventualConsistency: false, ct);
            return new GraphUserSweep(items.Select(ParseUser).ToDictionary(u => u.Id), SignInActivityAvailable: false);
        }
    }

    private static GraphDirectoryObject ParseDirObject(JsonElement el) => new(
        S(el, "id")!, S(el, "@odata.type"), S(el, "displayName"), S(el, "appId"),
        S(el, "userPrincipalName"), S(el, "userType"));

    public async Task<IReadOnlyList<GraphDirectoryObject>> GetGroupTransitiveMembersAsync(
        int credentialId, string groupId, CancellationToken ct = default)
    {
        var (http, token) = await ClientAsync(credentialId, ct);
        var url = $"{GraphBase}/groups/{groupId}/transitiveMembers?$select=id,displayName,userPrincipalName,userType,accountEnabled&$top=999";
        var items = await GetPagedAsync(http, token, url, eventualConsistency: true, ct);
        return items.Select(ParseDirObject).ToList();
    }

    public async Task<IReadOnlyList<GraphDirectoryObject>> GetGlobalAdminsAsync(int credentialId, CancellationToken ct = default)
    {
        var (http, token) = await ClientAsync(credentialId, ct);
        var roles = await GetJsonAsync(http, token,
            $"{GraphBase}/directoryRoles?$filter=displayName eq 'Global Administrator'", false, ct);
        var roleId = roles.TryGetProperty("value", out var v) && v.GetArrayLength() > 0
            ? S(v[0], "id") : null;

        if (roleId is not null)
        {
            var members = await GetPagedAsync(http, token,
                $"{GraphBase}/directoryRoles/{roleId}/members?$select=id,displayName,userPrincipalName,userType,accountEnabled&$top=999",
                false, ct);
            return members.Select(ParseDirObject).ToList();
        }

        // Fallback: rol no activado → roleAssignments por roleDefinitionId fijo de GA.
        var assigns = await GetPagedAsync(http, token,
            $"{GraphBase}/roleManagement/directory/roleAssignments?$filter=roleDefinitionId eq '{GaRoleDefinitionId}'&$expand=principal",
            false, ct);
        return assigns
            .Where(a => a.TryGetProperty("principal", out var p) && p.ValueKind == JsonValueKind.Object)
            .Select(a => ParseDirObject(a.GetProperty("principal")))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, GraphDirectoryObject>> GetByIdsAsync(
        int credentialId, IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var map = new Dictionary<string, GraphDirectoryObject>();
        if (ids.Count == 0) return map;
        var (http, token) = await ClientAsync(credentialId, ct);

        foreach (var chunk in ids.Chunk(1000))
        {
            var body = JsonSerializer.Serialize(new { ids = chunk, types = new[] { "user", "group", "servicePrincipal" } });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{GraphBase}/directoryObjects/getByIds")
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("value", out var value))
                foreach (var el in value.EnumerateArray())
                {
                    var obj = ParseDirObject(el);
                    map[obj.Id] = obj;
                }
        }
        return map;
    }

    public async Task<string> GetMfaStatusAsync(int credentialId, string userId, CancellationToken ct = default)
    {
        try
        {
            var (http, token) = await ClientAsync(credentialId, ct);
            var root = await GetJsonAsync(http, token, $"{GraphBase}/users/{userId}/authentication/methods", false, ct);
            if (!root.TryGetProperty("value", out var methods)) return "unavailable";
            foreach (var m in methods.EnumerateArray())
            {
                var type = S(m, "@odata.type");
                if (type is not "#microsoft.graph.passwordAuthenticationMethod"
                         and not "#microsoft.graph.emailAuthenticationMethod")
                    return "enabled";
            }
            return "disabled";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return "unavailable"; }
    }
}
