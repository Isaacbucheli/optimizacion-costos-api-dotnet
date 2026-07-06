using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Azure.Core;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Features.AzureIntegration.UserSessions;

public sealed record LighthouseSubscription(string SubscriptionId, string? DisplayName);
public sealed record LighthouseClientGroup(string TenantId, string ClientName, IReadOnlyList<LighthouseSubscription> Subscriptions);

/// <summary>
/// Catálogo de clientes delegados por Lighthouse visto por la sesión del usuario:
/// GET /subscriptions (paginado) agrupado por homeTenantId + nombre real del cliente vía
/// Microsoft.ManagedServices/registrationDefinitions (manageeTenantName), 1 llamada por tenant.
/// </summary>
public interface ILighthouseCatalogService
{
    Task<IReadOnlyList<LighthouseClientGroup>> GetClientsAsync(string email, bool refresh = false, CancellationToken ct = default);
}

public sealed class LighthouseCatalogService(
    IAzureUserSessionService sessions,
    IHttpClientFactory httpFactory,
    AppConfig config,
    ILogger<LighthouseCatalogService> logger) : ILighthouseCatalogService
{
    private const string ArmScope = "https://management.azure.com/.default";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private const int MaxParallelNameLookups = 8;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<LighthouseClientGroup> Data)> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<LighthouseClientGroup>> GetClientsAsync(string email, bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && _cache.TryGetValue(email, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheTtl)
            return hit.Data;

        var credential = sessions.GetCredentialForEmail(email);
        var token = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);

        async Task<JsonNode?> GetAsync(string url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        }

        // 1) Suscripciones (paginado con nextLink).
        var subs = new List<(string Id, string? Name, string Tenant)>();
        var url = "https://management.azure.com/subscriptions?api-version=2020-01-01";
        while (url is not null)
        {
            var page = await GetAsync(url)
                ?? throw new InvalidOperationException("No se pudo listar suscripciones con la sesión");
            foreach (var item in page["value"]!.AsArray())
            {
                var sid = item?["subscriptionId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(sid)) continue;
                subs.Add((sid,
                    item!["displayName"]?.GetValue<string>(),
                    item["homeTenantId"]?.GetValue<string>() ?? item["tenantId"]?.GetValue<string>() ?? "?"));
            }
            url = page["nextLink"]?.GetValue<string>();
        }

        // 2) Nombre del cliente por tenant distinto (paralelo con tope).
        var byTenant = subs.GroupBy(s => s.Tenant).ToList();
        var names = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(MaxParallelNameLookups);
        await Task.WhenAll(byTenant.Select(async g =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (string.Equals(g.Key, config.UserSessionTenantId, StringComparison.OrdinalIgnoreCase))
                {
                    names[g.Key] = "Business IT (tenant propio)";
                    return;
                }
                var defs = await GetAsync(
                    $"https://management.azure.com/subscriptions/{g.First().Id}/providers/Microsoft.ManagedServices/registrationDefinitions?api-version=2022-10-01");
                var name = defs?["value"]?.AsArray()
                    .Select(d => d?["properties"]?["manageeTenantName"]?.GetValue<string>())
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n));
                var shortId = g.Key.Length >= 9 ? g.Key[..9] : g.Key;
                names[g.Key] = name ?? $"(sin nombre, tenant {shortId}...)";
            }
            finally { gate.Release(); }
        }));

        var groups = byTenant
            .Select(g => new LighthouseClientGroup(
                g.Key, names[g.Key],
                g.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                 .Select(s => new LighthouseSubscription(s.Id, s.Name)).ToList()))
            .OrderBy(g => g.ClientName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger.LogInformation("Catálogo Lighthouse: {Subs} subs / {Tenants} clientes para {Email}",
            subs.Count, groups.Count, email);
        _cache[email] = (DateTimeOffset.UtcNow, groups);
        return groups;
    }
}
