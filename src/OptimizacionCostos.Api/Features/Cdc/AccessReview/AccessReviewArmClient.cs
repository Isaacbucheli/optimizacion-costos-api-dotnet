using System.Text.Json;
using Azure.Core;
using OptimizacionCostos.Api.Features.AzureIntegration;

namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

public sealed record ArmRoleAssignment(
    string Scope, string ScopeLevel, string PrincipalId, string PrincipalType,
    string RoleDefinitionId, string RoleName);

public interface IAccessReviewArmClient
{
    /// <summary>Role assignments de la suscripción SIN filtro atScope() → incluye RG y recurso.</summary>
    Task<IReadOnlyList<ArmRoleAssignment>> GetRoleAssignmentsAsync(int credentialId, string subscriptionId, CancellationToken ct = default);
}

public static class AccessReviewScope
{
    /// <summary>management_group | subscription | resource_group | resource | root.</summary>
    public static string Level(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope == "/") return "root";
        var s = scope.ToLowerInvariant();
        if (s.Contains("/managementgroups/")) return "management_group";
        if (!s.Contains("/resourcegroups/")) return "subscription";
        // Con /providers/ después del RG es un recurso puntual.
        var rgIdx = s.IndexOf("/resourcegroups/", StringComparison.Ordinal);
        return s.IndexOf("/providers/", rgIdx, StringComparison.Ordinal) >= 0 ? "resource" : "resource_group";
    }
}

public sealed class AccessReviewArmClient(IAzureCredentialFactory credentials, IHttpClientFactory httpFactory)
    : IAccessReviewArmClient
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ArmBase = "https://management.azure.com";
    private const string AuthApi = "2022-04-01";
    private const int MaxPages = 50;

    private async Task<(HttpClient Http, string Token)> ClientAsync(int credentialId, CancellationToken ct)
    {
        var cred = await credentials.GetClientSecretCredentialAsync(credentialId, ct);
        var token = await cred.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        return (http, token.Token);
    }

    private static async Task<List<JsonElement>> GetPagedAsync(HttpClient http, string token, string url, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        string? next = url;
        var page = 0;
        while (next is not null && page++ < MaxPages)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, next);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("value", out var value))
                foreach (var el in value.EnumerateArray()) items.Add(el.Clone());
            next = doc.RootElement.TryGetProperty("nextLink", out var nl) && nl.ValueKind == JsonValueKind.String
                ? nl.GetString() : null;
        }
        return items;
    }

    private static string? S(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public async Task<IReadOnlyList<ArmRoleAssignment>> GetRoleAssignmentsAsync(
        int credentialId, string subscriptionId, CancellationToken ct = default)
    {
        var (http, token) = await ClientAsync(credentialId, ct);

        // 1) Definiciones de rol → nombre por id (case-insensitive).
        var roleNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defs = await GetPagedAsync(http, token,
            $"{ArmBase}/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions?api-version={AuthApi}", ct);
        foreach (var d in defs)
        {
            var id = S(d, "id");
            var name = d.TryGetProperty("properties", out var p) ? S(p, "roleName") : null;
            if (id is not null && name is not null) roleNames[id] = name;
        }

        // 2) Asignaciones SIN atScope(): todas (heredadas de MG, sub, RG y recurso).
        var assigns = await GetPagedAsync(http, token,
            $"{ArmBase}/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleAssignments?api-version={AuthApi}", ct);

        var rows = new List<ArmRoleAssignment>();
        foreach (var a in assigns)
        {
            if (!a.TryGetProperty("properties", out var p)) continue;
            var principalId = S(p, "principalId");
            var principalType = S(p, "principalType");
            var roleDefId = S(p, "roleDefinitionId");
            var scope = S(p, "scope");
            if (principalId is null || roleDefId is null || scope is null) continue;
            if (principalType is not ("User" or "Group" or "ServicePrincipal")) continue;

            var roleName = roleNames.TryGetValue(roleDefId, out var rn) ? rn : roleDefId;
            rows.Add(new ArmRoleAssignment(scope, AccessReviewScope.Level(scope),
                principalId, principalType, roleDefId, roleName));
        }
        return rows;
    }
}
