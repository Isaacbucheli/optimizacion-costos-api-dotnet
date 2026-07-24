using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace OptimizacionCostos.Api.Features.Inventory;

/// <summary>Resultado del enriquecimiento: cuentas que superan el corte + advertencias visibles.</summary>
public sealed record StorageFilesEnrichment(IReadOnlyList<RgRow> Kept, IReadOnlyList<string> Warnings);

/// <summary>
/// Enriquecimiento ARM del servicio storage_files (spec 2026-07-24): por cada storage
/// account del KQL lista sus fileshares (management plane, $expand=stats) y calcula la
/// capacidad FACTURABLE por tier — estándar = GiB usados (shareUsageBytes); premium
/// (kind FileStorage) = GiB de cuota (shareQuota), fiel a cómo factura Azure. Solo se
/// conservan las cuentas cuya capacidad facturable SUPERA MinBillableGib (corte estricto:
/// 10,240 GiB no entra). Fallo por cuenta → se omite CON advertencia (nunca cero silencioso).
/// </summary>
public interface IStorageFilesEnricher
{
    Task<StorageFilesEnrichment> EnrichAsync(
        TokenCredential credential, IReadOnlyList<JsonNode> rows, CancellationToken ct);
}

public sealed class StorageFilesEnricher(
    IHttpClientFactory httpFactory,
    ILogger<StorageFilesEnricher> logger) : IStorageFilesEnricher
{
    /// <summary>Corte del spec: 10 TiB exactos en GiB. Estricto: > entra, == no entra.</summary>
    internal const double MinBillableGib = 10240.0;

    private const string ArmScope = "https://management.azure.com/.default";
    private const string ApiVersion = "2023-05-01"; // versión estable de fileServices/shares
    private const double BytesPerGib = 1024d * 1024 * 1024;

    public async Task<StorageFilesEnrichment> EnrichAsync(
        TokenCredential credential, IReadOnlyList<JsonNode> rows, CancellationToken ct)
    {
        var kept = new List<RgRow>();
        var warnings = new List<string>();
        if (rows.Count == 0)
        {
            return new StorageFilesEnrichment(kept, warnings);
        }

        var token = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);

        foreach (var node in rows)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }
            var row = new RgRow(obj);
            var id = row.Str("id");
            var name = row.Str("name") ?? id ?? "(sin nombre)";
            if (string.IsNullOrEmpty(id))
            {
                warnings.Add($"{name}: fila de Resource Graph sin id; cuenta omitida");
                continue;
            }

            try
            {
                var shares = await ListSharesAsync(http, token.Token, id, ct);
                if (shares.Count == 0)
                {
                    continue; // cuenta sin fileshares → no se inventaría (esperado, mayoría blob)
                }
                var isPremium = string.Equals(row.Str("kind"), "FileStorage", StringComparison.OrdinalIgnoreCase);
                var agg = Aggregate(shares, isPremium);
                if (agg.BillableGib <= MinBillableGib)
                {
                    continue; // corte estricto del spec
                }

                obj["shareCount"] = agg.ShareCount;
                obj["usedGib"] = Math.Round(agg.UsedGib, 2);
                obj["provisionedGib"] = Math.Round(agg.ProvisionedGib, 2);
                obj["billableGib"] = Math.Round(agg.BillableGib, 2);
                obj["tierBreakdownJson"] = JsonSerializer.Serialize(agg.TierGib);
                kept.Add(new RgRow(obj));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fileshares no consultados account={Name} type={Type}", name, ex.GetType().Name);
                warnings.Add($"{name}: fileshares no consultados ({ex.GetType().Name}); cuenta omitida del análisis");
            }
        }

        return new StorageFilesEnrichment(kept, warnings);
    }

    internal sealed record ShareInfo(long UsageBytes, int QuotaGib, string? AccessTier);

    internal sealed record Aggregated(
        int ShareCount, double UsedGib, double ProvisionedGib, double BillableGib,
        IReadOnlyDictionary<string, double> TierGib);

    /// <summary>Agregado puro por cuenta (testeable sin HTTP): facturable por tier.</summary>
    internal static Aggregated Aggregate(IReadOnlyList<ShareInfo> shares, bool isPremium)
    {
        double used = 0, provisioned = 0, billable = 0;
        var tiers = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var s in shares)
        {
            var usedGib = s.UsageBytes / BytesPerGib;
            used += usedGib;
            provisioned += s.QuotaGib;
            var shareBillable = isPremium ? s.QuotaGib : usedGib;
            billable += shareBillable;
            var tier = isPremium ? "premium" : (s.AccessTier ?? "").ToLowerInvariant() switch
            {
                "hot" => "hot",
                "cool" => "cool",
                _ => "transaction_optimized", // default de shares estándar (incluye GPv1)
            };
            tiers[tier] = Math.Round(tiers.GetValueOrDefault(tier) + shareBillable, 2);
        }
        return new Aggregated(shares.Count, used, provisioned, billable, tiers);
    }

    private static async Task<List<ShareInfo>> ListSharesAsync(
        HttpClient http, string bearerToken, string accountId, CancellationToken ct)
    {
        var shares = new List<ShareInfo>();
        var url = $"https://management.azure.com{accountId}/fileServices/default/shares"
            + $"?api-version={ApiVersion}&$expand=stats";

        while (!string.IsNullOrEmpty(url))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (!item.TryGetProperty("properties", out var props))
                    {
                        continue;
                    }
                    var usage = props.TryGetProperty("shareUsageBytes", out var u) && u.ValueKind == JsonValueKind.Number
                        ? u.GetInt64() : 0L;
                    var quota = props.TryGetProperty("shareQuota", out var q) && q.ValueKind == JsonValueKind.Number
                        ? q.GetInt32() : 0;
                    var tier = props.TryGetProperty("accessTier", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() : null;
                    shares.Add(new ShareInfo(usage, quota, tier));
                }
            }
            url = doc.RootElement.TryGetProperty("nextLink", out var nl) && nl.ValueKind == JsonValueKind.String
                ? nl.GetString() : null;
        }
        return shares;
    }
}
