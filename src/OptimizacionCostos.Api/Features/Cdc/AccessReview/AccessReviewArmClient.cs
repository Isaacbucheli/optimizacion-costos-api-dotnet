using System.Text.Json;
using Azure.Core;
using OptimizacionCostos.Api.Features.AzureIntegration;

namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

/// <summary>RoleClass es null cuando la definición de rol no está en la suscripción (definida en
/// otra rama de management group): "sin clasificar", nunca asumir lectura.</summary>
public sealed record ArmRoleAssignment(
    string Scope, string ScopeLevel, string PrincipalId, string PrincipalType,
    string RoleDefinitionId, string RoleName, string? RoleClass = null, bool IsCustomRole = false);

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

public sealed class AccessReviewArmClient(
    IAzureCredentialFactory credentials, IHttpClientFactory httpFactory,
    ILogger<AccessReviewArmClient> logger) : IAccessReviewArmClient
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

    private async Task<List<JsonElement>> GetPagedAsync(HttpClient http, string token, string url, CancellationToken ct)
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
        // Cortar en el tope y no decirlo se lee como "está todo": el conteo saldría por debajo del
        // portal sin ninguna señal de por qué.
        if (next is not null)
            logger.LogWarning("Paginación ARM truncada en {MaxPages} páginas para {Url}: quedaron resultados sin leer",
                MaxPages, url);
        return items;
    }

    private static string? S(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public async Task<IReadOnlyList<ArmRoleAssignment>> GetRoleAssignmentsAsync(
        int credentialId, string subscriptionId, CancellationToken ct = default)
    {
        var (http, token) = await ClientAsync(credentialId, ct);

        // 1) Definiciones de rol → nombre y clase de privilegio por id (case-insensitive). La clase
        //    se deriva de los permisos del propio payload, que ya se está descargando.
        var roleNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roleClasses = new Dictionary<string, RoleClassification>(StringComparer.OrdinalIgnoreCase);
        var defs = await GetPagedAsync(http, token,
            $"{ArmBase}/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions?api-version={AuthApi}", ct);
        foreach (var d in defs)
        {
            var id = S(d, "id");
            if (id is null || !d.TryGetProperty("properties", out var p)) continue;
            if (S(p, "roleName") is { } name) roleNames[id] = name;
            roleClasses[id] = AccessReviewRoleClassifier.Classify(p);
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
            // Se conserva TODO tipo de principal. Descartar los que no son User/Group/ServicePrincipal
            // dejaba invisible, entre otros, al ForeignGroup (grupo administrado desde otro tenant) y
            // hacía que el total no cuadrara con el portal. Sin principalType → "Unknown".
            var pType = principalType ?? "Unknown";

            var roleName = roleNames.TryGetValue(roleDefId, out var rn) ? rn : roleDefId;
            var cls = roleClasses.GetValueOrDefault(roleDefId);
            rows.Add(new ArmRoleAssignment(scope, AccessReviewScope.Level(scope),
                principalId, pType, roleDefId, roleName, cls?.RoleClass, cls?.IsCustom ?? false));
        }
        return rows;
    }
}
