using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using OptimizacionCostos.Api.Features.AzureIntegration;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Cliente REST de la Azure Advisor API. Port 1:1 de app/waf/advisor_api.py.
/// Token ARM por credencial (IAzureCredentialFactory), HttpClient via IHttpClientFactory.
/// Reintenta 429/500/502/503/504 (hasta 3) leyendo Retry-After. Timeout 60s por request.
/// </summary>
public sealed partial class AdvisorApiClient(
    IAzureCredentialFactory credentials,
    IHttpClientFactory httpFactory) : IAdvisorApiClient
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ArmBaseUrl = "https://management.azure.com";

    [GeneratedRegex(@"/resourceGroups/([^/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceGroupRegex();

    // ---------------------------------------------------------------------
    // Token + HttpClient
    // ---------------------------------------------------------------------

    private async Task<(HttpClient Http, string Token)> ClientAsync(int credentialId, CancellationToken ct)
    {
        var cred = await credentials.GetClientSecretCredentialAsync(credentialId, ct);
        var token = await cred.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60); // httpx.Client(timeout=60.0)
        return (http, token.Token);
    }

    // ---------------------------------------------------------------------
    // 1) Advisor Score por suscripción (fetch_subscription_advisor_score)
    // ---------------------------------------------------------------------

    public async Task<IReadOnlyDictionary<int, AdvisorScoreData>> FetchSubscriptionScoreAsync(
        int credentialId, string subscriptionId, CancellationToken ct = default)
    {
        var (http, token) = await ClientAsync(credentialId, ct);
        var url = ArmUrl($"/subscriptions/{subscriptionId}/providers/Microsoft.Advisor/advisorScore")
                + $"?api-version={WafConstants.AdvisorScoreApiVersion}";

        using var response = await RequestWithRetryAsync(http, token, HttpMethod.Get, url, ct);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new AdvisorApiException($"Advisor score failed HTTP {(int)response.StatusCode}: {await SafeErrorAsync(response, ct)}");

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var scores = new Dictionary<int, AdvisorScoreData>();

        if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                // category = _normalize(item["name"]).casefold().replace(" ", "")
                var category = Normalize(Str(item, "name")).ToLowerInvariant().Replace(" ", "");
                // Mapeo SIN default (a diferencia de la ingesta): categorías no mapeables se omiten.
                if (!WafConstants.CategoryToPillar.TryGetValue(category, out var pillar))
                    continue;

                var last = item.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                    && props.TryGetProperty("lastRefreshedScore", out var lr) && lr.ValueKind == JsonValueKind.Object
                    ? lr : default;
                if (last.ValueKind != JsonValueKind.Object) continue;

                if (!TryGetDouble(last, "score", out var scoreValue))
                    continue; // score None o no numérico -> skip

                var weight = TryGetDouble(last, "consumptionUnits", out var w) ? w : 0.0;

                scores[pillar] = new AdvisorScoreData(
                    Score: (decimal)Math.Round(scoreValue, 2),
                    Weight: (decimal)Math.Max(0.0, weight));
            }
        }
        return scores;
    }

    // ---------------------------------------------------------------------
    // Advisor Score HISTÓRICO por suscripción (timeSeries del mismo endpoint advisorScore)
    // ---------------------------------------------------------------------

    public async Task<IReadOnlyList<SubscriptionScoreHistory>> FetchSubscriptionScoreHistoryAsync(
        int credentialId, string subscriptionId, CancellationToken ct = default)
    {
        var (http, token) = await ClientAsync(credentialId, ct);
        var url = ArmUrl($"/subscriptions/{subscriptionId}/providers/Microsoft.Advisor/advisorScore")
                + $"?api-version={WafConstants.AdvisorScoreApiVersion}";

        using var response = await RequestWithRetryAsync(http, token, HttpMethod.Get, url, ct);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new AdvisorApiException($"Advisor score history failed HTTP {(int)response.StatusCode}: {await SafeErrorAsync(response, ct)}");

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        return ParseScoreHistory(doc.RootElement);
    }

    /// <summary>
    /// Extrae el timeSeries del endpoint advisorScore. Devuelve una entrada por nivel de agregación
    /// (D/W/M) con series 0=global ("Advisor") y 1..5=pilar. Puro y testeable.
    /// </summary>
    internal static IReadOnlyList<SubscriptionScoreHistory> ParseScoreHistory(JsonElement root)
    {
        // granularidad -> (serie 0..5 -> puntos)
        var byGran = new Dictionary<char, Dictionary<int, List<AdvisorHistoryPoint>>>();

        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var category = Normalize(Str(item, "name")).ToLowerInvariant().Replace(" ", "");
                int seriesIndex;
                if (category == "advisor") seriesIndex = 0;
                else if (WafConstants.CategoryToPillar.TryGetValue(category, out var pillar)) seriesIndex = pillar;
                else continue;

                if (!item.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) continue;
                if (!props.TryGetProperty("timeSeries", out var ts) || ts.ValueKind != JsonValueKind.Array) continue;

                foreach (var seriesEl in ts.EnumerateArray())
                {
                    // Azure devuelve "Daily"/"Weekly"/"Monthly" en la práctica; el doc REST muestra
                    // "day"/"week"/"month". Aceptar AMBAS formas (ojo: "daily" NO empieza por "day").
                    var level = Normalize(Str(seriesEl, "aggregationLevel")).ToLowerInvariant();
                    var gran = level is "day" or "daily" ? 'D'
                             : level is "week" or "weekly" ? 'W'
                             : level is "month" or "monthly" ? 'M'
                             : '\0';
                    if (gran == '\0') continue;
                    if (!seriesEl.TryGetProperty("scoreHistory", out var hist) || hist.ValueKind != JsonValueKind.Array) continue;

                    var bucket = byGran.TryGetValue(gran, out var b) ? b : (byGran[gran] = new());
                    var points = bucket.TryGetValue(seriesIndex, out var p) ? p : (bucket[seriesIndex] = new());

                    foreach (var pt in hist.EnumerateArray())
                    {
                        if (!TryGetDouble(pt, "score", out var score)) continue;
                        var rawDate = Str(pt, "date");
                        if (string.IsNullOrEmpty(rawDate)
                            || !DateTimeOffset.TryParse(rawDate, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal, out var dto))
                            continue;
                        var weight = TryGetDouble(pt, "consumptionUnits", out var w) ? w : 0.0;
                        points.Add(new AdvisorHistoryPoint(
                            Date: DateOnly.FromDateTime(dto.UtcDateTime),
                            Score: (decimal)Math.Round(score, 2),
                            Weight: (decimal)Math.Max(0.0, weight)));
                    }
                }
            }
        }

        return byGran.Select(g => new SubscriptionScoreHistory(
                g.Key,
                g.Value.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<AdvisorHistoryPoint>)kv.Value)))
            .ToList();
    }

    // ---------------------------------------------------------------------
    // 2) Generar + listar recomendaciones (generate_and_list_subscription_recommendations)
    // ---------------------------------------------------------------------

    public async Task<(IReadOnlyList<AdvisorRow> Rows, WafIngestionMetrics Metrics)> GenerateAndListRecommendationsAsync(
        int credentialId, string subscriptionId, string subscriptionName,
        int timeoutSeconds = 600, int pageSize = 200, CancellationToken ct = default)
    {
        var (http, token) = await ClientAsync(credentialId, ct);

        // Fase 1: trigger (202 + Location)
        var generateUrl = ArmUrl($"/subscriptions/{subscriptionId}/providers/Microsoft.Advisor/generateRecommendations")
                        + $"?api-version={WafConstants.AdvisorGenerateApiVersion}";
        using var generate = await RequestWithRetryAsync(http, token, HttpMethod.Post, generateUrl, ct);
        if (generate.StatusCode != HttpStatusCode.Accepted)
            throw new AdvisorApiException($"Advisor generate failed HTTP {(int)generate.StatusCode}: {await SafeErrorAsync(generate, ct)}");

        var statusUrl = generate.Headers.Location?.OriginalString;
        if (string.IsNullOrEmpty(statusUrl))
            throw new AdvisorApiException("Advisor generate did not return a Location header");
        statusUrl = statusUrl.StartsWith('/') ? ArmUrl(statusUrl) : ValidateNextLink(statusUrl);

        // Fase 2: polling hasta 204
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var waitSeconds = RetryAfterSeconds(generate, 10);
        while (true)
        {
            if (DateTime.UtcNow > deadline)
                throw new AdvisorApiException("Advisor generation timed out");
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
            using var status = await RequestWithRetryAsync(http, token, HttpMethod.Get, statusUrl, ct);
            if (status.StatusCode == HttpStatusCode.NoContent) break;
            if (status.StatusCode != HttpStatusCode.Accepted)
                throw new AdvisorApiException($"Advisor status failed HTTP {(int)status.StatusCode}: {await SafeErrorAsync(status, ct)}");
            waitSeconds = RetryAfterSeconds(status, 10);
        }

        // Fase 3: listar paginado (con manejo resiliente del nextLink; ver ListAllPagesAsync).
        var (items, truncated) = await ListAllPagesAsync(http, token, subscriptionId, pageSize, ct);

        var (rows, baseMetrics) = ItemsToRows(items, subscriptionId, subscriptionName);
        var metrics = baseMetrics with
        {
            AdvisorItems = items.Count,
            SubscriptionId = subscriptionId,
            SubscriptionName = subscriptionName,
            PaginationTruncated = truncated,
        };
        return (rows, metrics);
    }

    // ---------------------------------------------------------------------
    // Parsing de items a AdvisorRow (helpers puros, testeables)
    // ---------------------------------------------------------------------

    /// <summary>Port de advisor_api_items_to_rows: dedup por (name,category,resource,subscription).</summary>
    internal static (IReadOnlyList<AdvisorRow> Rows, WafIngestionMetrics Metrics) ItemsToRows(
        IReadOnlyList<JsonElement> items, string subscriptionId, string subscriptionName)
    {
        var rows = new List<AdvisorRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        var duplicates = 0;

        foreach (var item in items)
        {
            var row = ItemToRow(item, subscriptionId, subscriptionName);
            if (string.IsNullOrEmpty(row.AdvisorName) || string.IsNullOrEmpty(row.AdvisorCategory))
            {
                skipped++;
                continue;
            }
            var key = string.Join(' ', new[]
            {
                row.AdvisorName.ToLowerInvariant(),
                row.AdvisorCategory.ToLowerInvariant(),
                row.ResourceName.ToLowerInvariant(),
                row.SubscriptionId.ToLowerInvariant(),
            });
            if (!seen.Add(key))
            {
                duplicates++;
                continue;
            }
            rows.Add(row);
        }

        var metrics = new WafIngestionMetrics(
            RowsTotal: rows.Count + skipped + duplicates,
            RowsProcessed: rows.Count,
            RowsSkipped: skipped,
            RowsDuplicateSkipped: duplicates,
            AdvisorItems: 0,
            SubscriptionId: subscriptionId,
            SubscriptionName: subscriptionName);
        return (rows, metrics);
    }

    /// <summary>Port de advisor_api_item_to_row: extrae y trunca cada campo del item ARM.</summary>
    internal static AdvisorRow ItemToRow(JsonElement item, string subscriptionId, string subscriptionName)
    {
        var properties = item.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        var shortDesc = properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty("shortDescription", out var sd) && sd.ValueKind == JsonValueKind.Object ? sd : default;
        var metadata = properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty("resourceMetadata", out var rm) && rm.ValueKind == JsonValueKind.Object ? rm : default;

        var resourceId = Normalize(Str(metadata, "resourceId") ?? Str(item, "id"));
        var impactedValue = Normalize(Str(properties, "impactedValue"));
        var impactedField = Normalize(Str(properties, "impactedField"));

        var advisorName = FirstNonEmpty(
            Normalize(Str(properties, "label")),
            Normalize(Str(shortDesc, "solution")),
            Normalize(Str(shortDesc, "problem")),
            Normalize(Str(properties, "description")),
            Normalize(Str(item, "name")));

        var resourceName = !string.IsNullOrEmpty(impactedValue) ? impactedValue : ResourceName(resourceId);
        var resourceType = !string.IsNullOrEmpty(impactedField) ? impactedField : ResourceType(resourceId);

        var category = Normalize(Str(properties, "category"));
        var impact = Normalize(Str(properties, "impact"));
        var subName = Normalize(subscriptionName);

        return new AdvisorRow(
            AdvisorName: Truncate(advisorName, 500),
            AdvisorCategory: Truncate(string.IsNullOrEmpty(category) ? "OperationalExcellence" : category, 100),
            BusinessImpact: Truncate(string.IsNullOrEmpty(impact) ? "Medium" : impact, 30),
            ResourceName: Truncate(resourceName, 512),
            ResourceType: Truncate(resourceType, 300),
            ResourceGroup: Truncate(ResourceGroup(resourceId), 255),
            SubscriptionId: Truncate(Normalize(subscriptionId), 100),
            SubscriptionName: Truncate(string.IsNullOrEmpty(subName) ? Normalize(subscriptionId) : subName, 255),
            AzureResourceId: Truncate(resourceId, 2048),
            AdditionalInfo: AdditionalInfo(properties, Normalize(Str(item, "name"))));
    }

    /// <summary>Port de _resource_group: /resourceGroups/(grupo) (case-insensitive).</summary>
    internal static string ResourceGroup(string? resourceId)
    {
        if (string.IsNullOrEmpty(resourceId)) return "";
        var m = ResourceGroupRegex().Match(resourceId);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Port de _resource_name: último segmento no vacío del resource id.</summary>
    internal static string ResourceName(string? resourceId)
    {
        if (string.IsNullOrEmpty(resourceId)) return "";
        var parts = resourceId.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : "";
    }

    /// <summary>Port de _resource_type: provider + tipos cada 2 segmentos desde "providers".</summary>
    internal static string ResourceType(string? resourceId)
    {
        var parts = (resourceId ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var lower = parts.Select(p => p.ToLowerInvariant()).ToArray();
        if (!lower.Contains("providers")) return "";
        for (var index = 0; index < parts.Length; index++)
        {
            if (lower[index] == "providers" && index + 2 < parts.Length)
            {
                var provider = parts[index + 1];
                var typeParts = new List<string> { provider };
                var cursor = index + 2;
                while (cursor < parts.Length)
                {
                    typeParts.Add(parts[cursor]);
                    cursor += 2;
                }
                return string.Join('/', typeParts);
            }
        }
        return "";
    }

    /// <summary>Port de _additional_info: JSON con las claves no estándar presentes (si las hay).</summary>
    internal static string? AdditionalInfo(JsonElement properties, string recommendationId)
    {
        string[] keys =
        [
            "recommendationTypeId", "control", "risk", "lastUpdated", "label", "description",
            "potentialBenefits", "learnMoreLink", "extendedProperties", "exposedMetadataProperties",
        ];
        var details = new Dictionary<string, object?>();
        if (properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in keys)
            {
                if (!properties.TryGetProperty(key, out var v)) continue;
                if (IsEmptyJson(v)) continue; // None/""/[]/{} se excluyen
                details[key] = JsonValueToObject(v);
            }
        }
        if (!string.IsNullOrEmpty(recommendationId))
            details["advisorRecommendationId"] = recommendationId;
        if (details.Count == 0) return null;
        return JsonSerializer.Serialize(details, JsonOpts); // ensure_ascii=False
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---------------------------------------------------------------------
    // HTTP helpers (retry / errors / pagination)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Lista TODAS las páginas de recomendaciones de una suscripción.
    /// - Primera página: si 404 con la versión de generate, reintenta UNA vez con la versión estable
    ///   (quirk de algunas suscripciones CSP). Si aun así falla, lanza (es una falla real).
    /// - Páginas de continuación (nextLink): si Azure devuelve un nextLink ROTO (404) o cualquier
    ///   error, se CONSERVA lo ya traído y se corta la paginación (resultado PARCIAL, no fatal).
    ///   Antes se descartaba todo y se lanzaba, dejando la suscripción entera sin recomendaciones.
    /// Devuelve las páginas traídas y un flag Truncated=true SOLO cuando una página de continuación
    /// falló y se cortó la paginación (resultado parcial). En el retorno normal Truncated=false.
    /// </summary>
    internal static async Task<(List<JsonElement> Items, bool Truncated)> ListAllPagesAsync(
        HttpClient http, string token, string subscriptionId, int pageSize, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var usedFallback = false;
        var isFirstPage = true;
        var truncated = false;
        string? listUrl = RecommendationsListUrl(subscriptionId, WafConstants.AdvisorGenerateApiVersion, pageSize);
        while (!string.IsNullOrEmpty(listUrl))
        {
            using var listed = await RequestWithRetryAsync(http, token, HttpMethod.Get, listUrl, ct);
            if (listed.StatusCode != HttpStatusCode.OK)
            {
                // Primera página en 404: reintentar una vez con la versión estable.
                if (isFirstPage && listed.StatusCode == HttpStatusCode.NotFound && !usedFallback)
                {
                    usedFallback = true;
                    listUrl = RecommendationsListUrl(subscriptionId, WafConstants.AdvisorRecommendationsFallbackApiVersion, pageSize);
                    continue;
                }
                // Primera página con error definitivo: falla real de la suscripción.
                if (isFirstPage)
                    throw new AdvisorApiException($"Advisor list failed HTTP {(int)listed.StatusCode}: {await SafeErrorAsync(listed, ct)}");
                // nextLink roto (404) u otro error en una página de continuación: parcial, conservar lo traído.
                truncated = true;
                break;
            }

            var body = await listed.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                foreach (var it in value.EnumerateArray())
                    items.Add(it.Clone());
            var nextLink = doc.RootElement.TryGetProperty("nextLink", out var nl) && nl.ValueKind == JsonValueKind.String
                ? nl.GetString() : null;
            listUrl = string.IsNullOrEmpty(nextLink) ? "" : ValidateNextLink(nextLink);
            isFirstPage = false;
        }
        return (items, truncated);
    }

    /// <summary>Port de _request_with_retry: reintenta 429/5xx hasta max_retries leyendo Retry-After.</summary>
    private static async Task<HttpResponseMessage> RequestWithRetryAsync(
        HttpClient http, string token, HttpMethod method, string url, CancellationToken ct, int maxRetries = 3)
    {
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            response?.Dispose();
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            response = await http.SendAsync(req, ct);
            var code = (int)response.StatusCode;
            if (code is not (429 or 500 or 502 or 503 or 504) || attempt >= maxRetries)
                return response;
            await Task.Delay(TimeSpan.FromSeconds(RetryAfterSeconds(response, 2 * (attempt + 1))), ct);
        }
        return response!;
    }

    /// <summary>Port de _retry_after_seconds: header Retry-After clamp [1,60], si no default.</summary>
    private static int RetryAfterSeconds(HttpResponseMessage response, int @default)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
            return @default;
        var raw = values.FirstOrDefault();
        if (string.IsNullOrEmpty(raw))
            return @default;
        return int.TryParse(raw, out var n) ? Math.Max(1, Math.Min(60, n)) : @default;
    }

    /// <summary>Port de _safe_error: extrae error.message/code del JSON o el texto crudo (max 500).</summary>
    private static async Task<string> SafeErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            text = "";
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                var message = Str(error, "message") ?? Str(error, "code") ?? text;
                return Truncate(message, 500);
            }
        }
        catch
        {
            // no es JSON, cae al texto crudo
        }
        if (!string.IsNullOrEmpty(text)) return Truncate(text, 500);
        return response.ReasonPhrase ?? "";
    }

    /// <summary>Port de _validate_next_link: solo https + management.azure.com.</summary>
    private static string ValidateNextLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || parsed.Scheme != "https"
            || !parsed.Host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase))
            throw new AdvisorApiException("Advisor returned an invalid pagination link");
        return url;
    }

    private static string ArmUrl(string path) => $"{ArmBaseUrl}{path}";

    private static string RecommendationsListUrl(string subscriptionId, string apiVersion, int pageSize) =>
        ArmUrl($"/subscriptions/{subscriptionId}/providers/Microsoft.Advisor/recommendations")
        + $"?api-version={apiVersion}&$top={pageSize}";

    // ---------------------------------------------------------------------
    // Helpers de texto / JSON
    // ---------------------------------------------------------------------

    /// <summary>
    /// Port de ingestion._normalize: " ".join(str(value or "").strip().split()) — colapsa runs de
    /// whitespace a un espacio. NO baja a minúsculas ni quita acentos (distinto de WafText.NormalizeText).
    /// </summary>
    internal static string Normalize(object? value)
    {
        var s = value?.ToString();
        if (string.IsNullOrEmpty(s)) return "";
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); // split en cualquier whitespace
        return string.Join(' ', parts);
    }

    private static string FirstNonEmpty(params string[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool TryGetDouble(JsonElement e, string key, out double value)
    {
        value = 0.0;
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) { value = n; return true; }
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
        { value = s; return true; }
        return false;
    }

    private static bool IsEmptyJson(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null => true,
        JsonValueKind.String => v.GetString() == "",
        JsonValueKind.Array => v.GetArrayLength() == 0,
        JsonValueKind.Object => !v.EnumerateObject().Any(),
        _ => false,
    };

    private static object? JsonValueToObject(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => JsonSerializer.Deserialize<object?>(v.GetRawText()),
    };
}
