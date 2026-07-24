using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace OptimizacionCostos.Api.Features.Inventory;

/// <summary>Resultado del enriquecimiento: cuentas que superan el corte + advertencias visibles.</summary>
public sealed record StorageFilesEnrichment(IReadOnlyList<RgRow> Kept, IReadOnlyList<string> Warnings);

/// <summary>
/// Enriquecimiento ARM del servicio storage_files (spec 2026-07-24, corregido tras E2E real
/// contra Azure): por cada storage account del KQL lista sus fileshares en DOS fases, porque
/// el endpoint LIST de ARM NO acepta <c>$expand=stats</c> (Azure responde 400
/// InvalidQueryParameterValue si se intenta; verificado empíricamente).
///   1) LIST (<c>GET .../shares?api-version=...</c>, paginado por nextLink): trae name,
///      shareQuota y accessTier — sin shareUsageBytes.
///   2) Por cada share (solo cuentas ESTÁNDAR): <c>GET .../shares/{name}?...&amp;$expand=stats</c>
///      trae shareUsageBytes. Las cuentas PREMIUM (kind FileStorage) se saltan este GET por
///      completo: facturan por cuota provisionada, no por uso, así que el uso no aporta nada
///      al cálculo (optimización correcta, no solo de performance).
/// Calcula la capacidad FACTURABLE por tier — estándar = GiB usados (shareUsageBytes); premium
/// = GiB de cuota (shareQuota), fiel a cómo factura Azure. Solo se conservan las cuentas cuya
/// capacidad facturable SUPERA MinBillableGib (corte estricto: 10,240 GiB no entra).
/// Fallo por cuenta (LIST) → se omite CON advertencia. Fallo de stats en UN share → esa cuenta
/// se sigue procesando (el resto de shares sí cuentan) pero queda advertencia visible de que
/// el total puede estar incompleto (nunca cero silencioso).
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

    /// <summary>Tope de paginación ARM (patrón de ResourceGraphRunner.MaxPages / AzureReservationsClient.MaxPages):
    /// un nextLink en bucle o malformado no debe colgar la cuenta indefinidamente.</summary>
    internal const int MaxPages = 50;

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
                var (listed, truncated) = await ListSharesAsync(http, token.Token, id, ct);
                if (truncated)
                {
                    // Conteo potencialmente incompleto: podría poner la cuenta al lado equivocado
                    // del corte de 10 TiB, así que debe llegar al usuario como advertencia visible
                    // (no solo un log), y se sigue procesando lo que sí se pudo traer.
                    warnings.Add($"{name}: listado de fileshares truncado en {MaxPages} páginas; el total puede estar incompleto");
                }
                if (listed.Count == 0)
                {
                    continue; // cuenta sin fileshares → no se inventaría (esperado, mayoría blob)
                }
                var isPremium = string.Equals(row.Str("kind"), "FileStorage", StringComparison.OrdinalIgnoreCase);
                var (shares, failedStats) = await FetchShareUsageAsync(http, token.Token, id, name, listed, isPremium, ct);
                if (failedStats > 0)
                {
                    // Igual que el truncado de paginación: uso incompleto puede cambiar de lado
                    // el corte de 10 TiB, así que se advierte en vez de fallar silenciosamente.
                    warnings.Add($"{name}: no se pudo leer el uso de {failedStats} de {listed.Count} shares; el total puede estar incompleto");
                }
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // cancelación real del import: propagar, no degradar a advertencia por cuenta
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

    /// <summary>Item crudo del LIST (fase 1): sin uso, porque el LIST de ARM no lo devuelve.</summary>
    internal sealed record ShareListItem(string Name, int QuotaGib, string? AccessTier);

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

    /// <summary>Fase 1: lista los fileshares de una cuenta paginando por nextLink. SIN
    /// <c>$expand=stats</c> — el LIST de ARM lo rechaza con 400 InvalidQueryParameterValue.
    /// <c>Truncated</c> es true cuando quedó un nextLink pendiente al alcanzar
    /// <see cref="MaxPages"/> (conteo incompleto).</summary>
    private static async Task<(List<ShareListItem> Shares, bool Truncated)> ListSharesAsync(
        HttpClient http, string bearerToken, string accountId, CancellationToken ct)
    {
        var shares = new List<ShareListItem>();
        var url = $"https://management.azure.com{accountId}/fileServices/default/shares"
            + $"?api-version={ApiVersion}";
        var page = 0;

        while (!string.IsNullOrEmpty(url) && page < MaxPages)
        {
            page++;
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
                    var shareName = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() : null;
                    if (string.IsNullOrEmpty(shareName))
                    {
                        continue; // sin nombre no se puede pedir el stats por-share (fase 2)
                    }
                    var quota = props.TryGetProperty("shareQuota", out var q) && q.ValueKind == JsonValueKind.Number
                        ? q.GetInt32() : 0;
                    var tier = props.TryGetProperty("accessTier", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() : null;
                    shares.Add(new ShareListItem(shareName, quota, tier));
                }
            }
            url = doc.RootElement.TryGetProperty("nextLink", out var nl) && nl.ValueKind == JsonValueKind.String
                ? nl.GetString() : null;
        }
        // Si el loop salió por el tope de páginas (no porque se acabó el nextLink), url sigue con valor.
        return (shares, !string.IsNullOrEmpty(url));
    }

    /// <summary>Fase 2: obtiene <c>shareUsageBytes</c> por-share con <c>$expand=stats</c> (el
    /// único endpoint que lo devuelve). Cuentas PREMIUM se saltan esta llamada por completo —
    /// facturan por cuota, no por uso, así que pedir el stats sería una llamada ARM desperdiciada.
    /// Un fallo en UN share no pierde la cuenta: cuenta como 0 y se reporta en <c>FailedCount</c>
    /// para que el llamador agregue la advertencia visible (uso posiblemente incompleto).</summary>
    private async Task<(List<ShareInfo> Shares, int FailedCount)> FetchShareUsageAsync(
        HttpClient http, string bearerToken, string accountId, string accountName,
        IReadOnlyList<ShareListItem> listed, bool isPremium, CancellationToken ct)
    {
        var shares = new List<ShareInfo>(listed.Count);
        var failed = 0;
        foreach (var item in listed)
        {
            long usageBytes = 0;
            if (!isPremium)
            {
                try
                {
                    usageBytes = await GetShareUsageBytesAsync(http, bearerToken, accountId, item.Name, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // cancelación real: propagar, no degradar a "share con fallo"
                }
                catch (Exception ex)
                {
                    failed++;
                    // El warning agregado (con conteo) ya llega al usuario desde EnrichAsync;
                    // este log solo aporta detalle técnico para diagnóstico.
                    logger.LogWarning(ex, "Stats de share no consultado account={Name} share={Share} type={Type}",
                        accountName, item.Name, ex.GetType().Name);
                }
            }
            shares.Add(new ShareInfo(usageBytes, item.QuotaGib, item.AccessTier));
        }
        return (shares, failed);
    }

    /// <summary>GET .../shares/{name}?api-version=...&amp;$expand=stats — único endpoint que
    /// devuelve <c>shareUsageBytes</c> (el LIST no lo trae).</summary>
    private static async Task<long> GetShareUsageBytesAsync(
        HttpClient http, string bearerToken, string accountId, string shareName, CancellationToken ct)
    {
        var url = $"https://management.azure.com{accountId}/fileServices/default/shares/{Uri.EscapeDataString(shareName)}"
            + $"?api-version={ApiVersion}&$expand=stats";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("properties", out var props)
            && props.TryGetProperty("shareUsageBytes", out var u) && u.ValueKind == JsonValueKind.Number)
        {
            return u.GetInt64();
        }
        return 0L;
    }
}
