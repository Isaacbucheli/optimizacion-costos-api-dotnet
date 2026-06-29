using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using OptimizacionCostos.Api.Features.AzureIntegration;

namespace OptimizacionCostos.Api.Features.Cdc;

/// <summary>Una reserva Azure (instancia reservada) ya proyectada.</summary>
public sealed record ReservationDto(
    string? ReservationId, int CredentialId, string? Name, string? Product, string? Region,
    int? Quantity, string? Term, string? TermLabel, string? State, string? AppliedScopeType,
    IReadOnlyList<string> AppliedScopes, string? ExpiresOn, int DaysRemaining, bool Expired, bool Expiring,
    string? UtilizationLast, string? Utilization7d);

/// <summary>Un recurso que consumió el beneficio de una reserva.</summary>
public sealed record ReservationConsumer(
    string InstanceId, string? ResourceName, string? ResourceGroup, string? SubscriptionId,
    string? SubscriptionName, string? SkuName, double UsedHours, string? LastSeen, int DaysSeen);

/// <summary>
/// Cliente REST de Azure Reservations / Consumption. Port de app/cdc/reservations.py.
/// Las reservas son a nivel tenant (billing scope): se consultan por credencial activa, no por
/// suscripción. Solo lectura (Reader). Usa B1 (IAzureCredentialFactory) para el token ARM.
/// </summary>
public interface IAzureReservationsClient
{
    Task<IReadOnlyList<ReservationDto>> FetchForCredentialAsync(int credentialId, int alertDays, DateOnly today, bool includeUtilization, CancellationToken ct = default);
    Task<(string Last, string Avg7d)> GetUtilizationAsync(int credentialId, string reservationId, CancellationToken ct = default);
    Task<IReadOnlyList<ReservationConsumer>> GetConsumersAsync(int credentialId, string reservationId, int days, CancellationToken ct = default);
}

public sealed class AzureReservationsClient(IAzureCredentialFactory credentials, IHttpClientFactory httpFactory) : IAzureReservationsClient
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string Arm = "https://management.azure.com";
    private const string ReservationsApi = "2022-11-01";
    private const string SummariesApi = "2023-05-01";
    private const string DetailsApi = "2024-08-01";
    private const int MaxPages = 50;

    internal static readonly IReadOnlyDictionary<string, string> TermLabels = new Dictionary<string, string>
    {
        ["P1Y"] = "1 año", ["P3Y"] = "3 años", ["P5Y"] = "5 años",
    };

    private async Task<(HttpClient Http, string Token)> ClientAsync(int credentialId, CancellationToken ct)
    {
        var cred = await credentials.GetClientSecretCredentialAsync(credentialId, ct);
        var token = await cred.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        return (http, token.Token);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient http, string token, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public async Task<IReadOnlyList<ReservationDto>> FetchForCredentialAsync(
        int credentialId, int alertDays, DateOnly today, bool includeUtilization, CancellationToken ct = default)
    {
        var (http, token) = await ClientAsync(credentialId, ct);
        var items = new List<ReservationDto>();
        string? url = $"{Arm}/providers/Microsoft.Capacity/reservations?api-version={ReservationsApi}";
        var page = 0;
        while (url is not null && page < MaxPages)
        {
            page++;
            var root = await GetJsonAsync(http, token, url, ct);
            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in value.EnumerateArray())
                {
                    var props = r.TryGetProperty("properties", out var p) ? p : default;
                    var expiry = ParseExpiry(Str(props, "expiryDateTime") ?? Str(props, "expiryDate"));
                    if (expiry is null) continue;
                    var daysRemaining = expiry.Value.DayNumber - today.DayNumber;
                    var term = Str(props, "term");
                    var reservationId = Str(r, "id");
                    string? utilLast = null, util7d = null;
                    if (includeUtilization && reservationId is not null)
                        (utilLast, util7d) = await GetUtilizationInternalAsync(http, token, reservationId, ct);
                    items.Add(new ReservationDto(
                        reservationId, credentialId, Str(props, "displayName"),
                        Str(r.TryGetProperty("sku", out var sku) ? sku : default, "name"),
                        Str(r, "location"), Int(props, "quantity"), term,
                        term is not null && TermLabels.TryGetValue(term, out var tl) ? tl : term,
                        Str(props, "displayProvisioningState") ?? Str(props, "provisioningState"),
                        Str(props, "appliedScopeType") ?? Str(props, "userFriendlyAppliedScopeType"),
                        StrList(props, "appliedScopes"),
                        expiry.Value.ToString("yyyy-MM-dd"), daysRemaining, daysRemaining < 0,
                        daysRemaining is >= 0 && daysRemaining <= alertDays, utilLast, util7d));
                }
            }
            url = root.TryGetProperty("nextLink", out var nl) && nl.ValueKind == JsonValueKind.String ? nl.GetString() : null;
        }
        return items;
    }

    public async Task<(string Last, string Avg7d)> GetUtilizationAsync(int credentialId, string reservationId, CancellationToken ct = default)
    {
        var (http, token) = await ClientAsync(credentialId, ct);
        return await GetUtilizationInternalAsync(http, token, reservationId, ct);
    }

    private static async Task<(string Last, string Avg7d)> GetUtilizationInternalAsync(HttpClient http, string token, string reservationId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var ini = now.AddDays(-7).ToString("yyyy-MM-dd");
        var fin = now.ToString("yyyy-MM-dd");
        var url = $"{Arm}{reservationId}/providers/Microsoft.Consumption/reservationSummaries"
                + $"?grain=daily&$filter=properties/UsageDate ge {ini} AND properties/UsageDate le {fin}&api-version={SummariesApi}";
        try
        {
            var root = await GetJsonAsync(http, token, url, ct);
            var vals = new List<double>();
            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                foreach (var d in value.EnumerateArray())
                {
                    var pct = d.TryGetProperty("properties", out var p) ? Dbl(p, "avgUtilizationPercentage") : null;
                    if (pct is not null) vals.Add(pct.Value);
                }
            if (vals.Count == 0) return ("0%", "0%");
            return ($"{Math.Round(vals[^1])}%", $"{Math.Round(vals.Average())}%");
        }
        catch
        {
            return ("n/d", "n/d");
        }
    }

    public async Task<IReadOnlyList<ReservationConsumer>> GetConsumersAsync(int credentialId, string reservationId, int days, CancellationToken ct = default)
    {
        var (http, token) = await ClientAsync(credentialId, ct);
        var now = DateTimeOffset.UtcNow;
        var ini = now.AddDays(-days).ToString("yyyy-MM-dd");
        var fin = now.ToString("yyyy-MM-dd");
        var byResource = new Dictionary<string, MutableConsumer>();
        string? url = $"{Arm}{reservationId}/providers/Microsoft.Consumption/reservationDetails"
                    + $"?api-version={DetailsApi}&$filter=properties/usageDate ge {ini} AND properties/usageDate le {fin}";
        var page = 0;
        while (url is not null && page < MaxPages)
        {
            page++;
            var root = await GetJsonAsync(http, token, url, ct);
            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                foreach (var rec in value.EnumerateArray())
                {
                    var p = rec.TryGetProperty("properties", out var pp) ? pp : default;
                    var iid = Str(p, "instanceId");
                    if (string.IsNullOrEmpty(iid)) continue;
                    var key = iid.ToLowerInvariant();
                    if (!byResource.TryGetValue(key, out var agg))
                    {
                        var parsed = ParseInstanceId(iid);
                        agg = byResource[key] = new MutableConsumer
                        {
                            InstanceId = iid, ResourceName = parsed.ResourceName, ResourceGroup = parsed.ResourceGroup,
                            SubscriptionId = parsed.SubscriptionId, SkuName = Str(p, "skuName"),
                        };
                    }
                    agg.UsedHours += Dbl(p, "usedHours") ?? 0;
                    var ud = Str(p, "usageDate");
                    if (!string.IsNullOrEmpty(ud))
                    {
                        var udDate = ud.Length >= 10 ? ud[..10] : ud;
                        if (agg.LastSeen is null || string.CompareOrdinal(udDate, agg.LastSeen) > 0) agg.LastSeen = udDate;
                        agg.DaysSeen++;
                    }
                    if (string.IsNullOrEmpty(agg.SkuName) && !string.IsNullOrEmpty(Str(p, "skuName"))) agg.SkuName = Str(p, "skuName");
                }
            url = root.TryGetProperty("nextLink", out var nl) && nl.ValueKind == JsonValueKind.String ? nl.GetString() : null;
        }
        return byResource.Values
            .OrderByDescending(c => c.UsedHours)
            .Select(c => new ReservationConsumer(c.InstanceId, c.ResourceName, c.ResourceGroup, c.SubscriptionId, null, c.SkuName, Math.Round(c.UsedHours, 1), c.LastSeen, c.DaysSeen))
            .ToList();
    }

    private sealed class MutableConsumer
    {
        public string InstanceId = ""; public string? ResourceName; public string? ResourceGroup;
        public string? SubscriptionId; public string? SkuName;
        public double UsedHours; public string? LastSeen; public int DaysSeen;
    }

    // -------------------- helpers puros (testeables) --------------------

    /// <summary>Port de _parse_expiry: ISO con/sin zona, o solo fecha → DateOnly (la fecha).</summary>
    internal static DateOnly? ParseExpiry(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return DateOnly.FromDateTime(dto.UtcDateTime);
        foreach (var fmt in new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss" })
            if (DateTime.TryParseExact(text, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return DateOnly.FromDateTime(dt);
        return null;
    }

    /// <summary>Port de _parse_instance_id: extrae subscription/resourceGroup/nombre de un Resource ID.</summary>
    internal static (string? SubscriptionId, string? ResourceGroup, string ResourceName) ParseInstanceId(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return (null, null, instanceId ?? "");
        var parts = instanceId.Trim('/').Split('/');
        var low = parts.Select(p => p.ToLowerInvariant()).ToList();
        string? sub = null, rg = null;
        var si = low.IndexOf("subscriptions");
        if (si >= 0 && si + 1 < parts.Length) sub = parts[si + 1];
        var ri = low.IndexOf("resourcegroups");
        if (ri >= 0 && ri + 1 < parts.Length) rg = parts[ri + 1];
        var name = parts.Length > 0 ? parts[^1] : instanceId;
        return (sub, rg, name);
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static double? Dbl(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null;
    private static IReadOnlyList<string> StrList(JsonElement e, string key)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
        return [];
    }
}
