using System.Net;
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
/// Calcula la capacidad FACTURABLE por tier — estándar = GiB usados (shareUsageBytes) MÁS el
/// diferencial de snapshots del share (Azure factura pay-as-you-go sobre "Data Stored", que
/// incluye el uso diferencial de los snapshots — ver <see cref="GetSnapshotDifferentialGibAsync"/>);
/// premium = GiB de cuota (shareQuota), fiel a cómo factura Azure. Solo se conservan las cuentas
/// cuya capacidad facturable SUPERA MinBillableGib (corte estricto: 10,240 GiB no entra) — el
/// diferencial de snapshots puede ser justo lo que empuja a una cuenta sobre el corte (E2E real:
/// cuentas de 10+ TiB casi siempre están protegidas por Azure Backup, que crea snapshots diarios).
/// Fallo por cuenta (LIST) → se omite CON advertencia, EXCEPTO cuando ARM responde 400
/// FeatureNotSupportedForAccount (cuenta sin servicio de Files, p.ej. Storage/Premium_LRS de solo
/// page-blob usada por Azure Site Recovery): eso se trata como "0 shares" y se omite EN SILENCIO,
/// para no diluir el canal de advertencias con falsas alarmas. Fallo de stats en UN share, un
/// <c>shareUsageBytes</c> AUSENTE en una respuesta 200 (ver <see cref="GetShareUsageBytesAsync"/>),
/// o un diferencial de snapshots que no se pudo leer o confirmar (excepción, sin datos, o
/// <c>errorCode</c> distinto de "Success" — ver <see cref="GetSnapshotDifferentialGibAsync"/>) →
/// esa cuenta se sigue procesando (el resto sí cuenta) pero queda advertencia visible de que el
/// total puede estar incompleto (nunca cero silencioso).
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
    private const string MetricsApiVersion = "2018-01-01"; // igual que ReportMetrics (Azure Monitor)
    private const double BytesPerGib = 1024d * 1024 * 1024;

    /// <summary>Código de error ARM cuando la cuenta no soporta el servicio de Files (verificado
    /// empíricamente: HTTP 400 con este código exacto en cuentas Storage/Premium_LRS de solo
    /// page-blob, ej. discos usados por Azure Site Recovery).</summary>
    private const string FeatureNotSupportedErrorCode = "FeatureNotSupportedForAccount";

    /// <summary>Ventana de búsqueda hacia atrás para el punto más reciente de la métrica
    /// FileShareSnapshotSize (Azure Monitor): la métrica de capacidad se emite ~1 vez al día con
    /// cierto rezago, así que se piden varios días para asegurar al menos un punto de dato.</summary>
    internal const int SnapshotMetricLookbackDays = 3;

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

                // Premium NO llama a esto: factura por cuota, el diferencial de snapshots no
                // aporta nada a esa cuenta (misma optimización que FetchShareUsageAsync).
                var snapshotGib = 0d;
                if (!isPremium)
                {
                    try
                    {
                        var gib = await GetSnapshotDifferentialGibAsync(http, token.Token, id, ct);
                        if (gib is null)
                        {
                            // Igual que failedStats: la métrica no trajo ningún punto de dato
                            // confiable (ver GetSnapshotDifferentialGibAsync) — nunca se trata como
                            // "0 GiB", se advierte y la cuenta se sigue procesando con lo que sí
                            // se pudo traer.
                            warnings.Add($"{name}: no se pudo leer el uso de snapshots de los fileshares; el total puede estar incompleto");
                        }
                        else
                        {
                            snapshotGib = gib.Value;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw; // cancelación real: propagar, no degradar a advertencia
                    }
                    catch (Exception ex)
                    {
                        // Igual que failedStats: un diferencial de snapshots incompleto puede
                        // cambiar de lado el corte de 10 TiB, así que se advierte (nunca cero
                        // silencioso) y la cuenta se sigue procesando con lo que sí se pudo traer.
                        logger.LogWarning(ex, "Uso de snapshots no consultado account={Name} type={Type}", name, ex.GetType().Name);
                        warnings.Add($"{name}: no se pudo leer el uso de snapshots de los fileshares; el total puede estar incompleto");
                    }
                }

                var agg = Aggregate(shares, isPremium, snapshotGib);
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
            catch (FileServiceNotSupportedException)
            {
                // Cuenta sin servicio de Files (ej. Storage/Premium_LRS de solo page-blob para
                // Azure Site Recovery): igual que 0 shares, se omite EN SILENCIO. No es una falla
                // real del import, así que no debe diluir el canal de advertencias.
                continue;
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

    /// <summary>Agregado puro por cuenta (testeable sin HTTP): facturable por tier.
    /// <paramref name="snapshotGib"/> es el diferencial de snapshots YA en GiB (cuenta completa,
    /// no hay desglose por-share/por-tier disponible en ninguna API de Azure verificada — ver
    /// <see cref="GetSnapshotDifferentialGibAsync"/>): con un solo tier presente se le suma
    /// completo (exacto); con varios tiers se reparte proporcional al GiB en vivo de cada uno
    /// (estimación razonable, documentada en la nota del calculador — no hay forma de obtener el
    /// dato exacto por tier). Premium ignora <paramref name="snapshotGib"/> (factura por cuota).</summary>
    internal static Aggregated Aggregate(IReadOnlyList<ShareInfo> shares, bool isPremium, double snapshotGib = 0)
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
            // Sin redondear dentro del loop (Change 3): el desglose por tier debe sumar EXACTO
            // a billable_gib (se ven lado a lado en el Excel); redondear por-share aquí introduce
            // deriva de centavos. Se redondea una sola vez al final.
            tiers[tier] = tiers.GetValueOrDefault(tier) + shareBillable;
        }

        if (!isPremium && snapshotGib > 0 && tiers.Count > 0)
        {
            billable += snapshotGib;
            if (tiers.Count == 1)
            {
                var onlyTier = tiers.Keys.First();
                tiers[onlyTier] += snapshotGib;
            }
            else
            {
                var totalLive = tiers.Values.Sum();
                if (totalLive > 0)
                {
                    foreach (var key in tiers.Keys.ToList())
                    {
                        tiers[key] += snapshotGib * (tiers[key] / totalLive);
                    }
                }
                else
                {
                    // Todos los shares en 0 GiB en vivo (caso extremo): reparte por partes iguales.
                    var each = snapshotGib / tiers.Count;
                    foreach (var key in tiers.Keys.ToList())
                    {
                        tiers[key] += each;
                    }
                }
            }
        }

        var roundedTiers = tiers.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2), StringComparer.Ordinal);
        return new Aggregated(shares.Count, used, provisioned, billable, roundedTiers);
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
            if (resp.StatusCode == HttpStatusCode.BadRequest)
            {
                // Hay que leer el body ANTES de EnsureSuccessStatusCode para distinguir "cuenta sin
                // servicio de Files" (verificado empíricamente: Storage/Premium_LRS de solo
                // page-blob, ej. discos de Azure Site Recovery) de cualquier otro 400 real, que
                // debe seguir lanzando y advirtiendo como hasta ahora.
                var errorBody = await resp.Content.ReadAsStringAsync(ct);
                if (IsFeatureNotSupportedForAccount(errorBody))
                {
                    throw new FileServiceNotSupportedException(
                        "La cuenta no soporta el servicio de Files (FeatureNotSupportedForAccount)");
                }
            }
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
    /// Un fallo en UN share (excepción HTTP, o una respuesta 200 sin el campo
    /// <c>shareUsageBytes</c> — ver <see cref="GetShareUsageBytesAsync"/>) no pierde la cuenta:
    /// cuenta como 0 y se reporta en <c>FailedCount</c> para que el llamador agregue la
    /// advertencia visible (uso posiblemente incompleto).</summary>
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
                    var bytes = await GetShareUsageBytesAsync(http, bearerToken, accountId, item.Name, ct);
                    if (bytes is null)
                    {
                        // El campo shareUsageBytes vino AUSENTE en una respuesta 200 (distinto de
                        // un fallo HTTP): indistinguible de "0 bytes usados" si se tratara como 0
                        // silencioso, y podría empujar la cuenta al lado equivocado del corte de
                        // 10 TiB sin ninguna señal visible. Se trata EXACTAMENTE igual que el
                        // camino de excepción: cuenta como fallo (failedStats) para que la
                        // advertencia agregada de EnrichAsync se dispare.
                        failed++;
                        logger.LogWarning(
                            "shareUsageBytes ausente en respuesta 200 account={Name} share={Share}",
                            accountName, item.Name);
                    }
                    else
                    {
                        usageBytes = bytes.Value;
                    }
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
    /// devuelve <c>shareUsageBytes</c> (el LIST no lo trae). Null (NO 0) cuando la respuesta 200
    /// no trae el campo — indistinguible de "0 bytes usados" para el llamador, que debe tratarlo
    /// como fallo (ver FetchShareUsageAsync), nunca como uso cero silencioso.</summary>
    private static async Task<long?> GetShareUsageBytesAsync(
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
        return null;
    }

    /// <summary>
    /// Diferencial de snapshots FACTURABLE de la cuenta completa (todos sus fileshares juntos),
    /// vía la métrica de Azure Monitor <c>FileShareSnapshotSize</c> del namespace
    /// <c>Microsoft.Storage/storageAccounts/fileServices</c> — la ÚNICA fuente verificada que
    /// devuelve el diferencial REAL facturado (no el tamaño lógico completo del share).
    ///
    /// Por qué NO se usa <c>GET .../shares?$expand=snapshots</c> + <c>GET .../shares/{name}?
    /// $expand=stats</c> con header <c>x-ms-snapshot</c> (que sí existe y sí devuelve
    /// <c>shareUsageBytes</c> por snapshot): verificado empíricamente contra una cuenta real con
    /// 31 snapshots diarios (Azure Backup) que <c>FileShareSnapshotSize</c> reportaba 0 bytes
    /// (el contenido del share no cambió en el período), pero CADA snapshot vía ese GET devolvía
    /// el tamaño lógico COMPLETO del share (209,691,648 bytes, idéntico en los 31). Sumar eso por
    /// snapshot habría sobreestimado el diferencial facturable en ~31× — Azure solo cobra el
    /// delta único de cada snapshot, no el tamaño total en cada punto en el tiempo (ver
    /// "Understand Azure Files billing": snapshots pay-as-you-go son "always differential").
    ///
    /// Limitación conocida (documentada también en la nota del calculador): esta métrica NO
    /// soporta desglose por-fileshare/por-tier pese a declarar la dimensión "FileShare" en
    /// metricDefinitions — se verificó que tanto <c>$filter=FileShare eq '*'</c> como un nombre
    /// de share exacto devuelven series vacías; solo el agregado <c>&lt;All&gt;</c> de la cuenta
    /// funciona (en la práctica, una sola serie). <see cref="Aggregate"/> reparte ese agregado
    /// entre los tiers ya calculados.
    ///
    /// Devuelve <b>null</b> (NO 0) cuando no se puede confiar en la lectura: ningún punto de
    /// dato trajo un valor <c>average</c> numérico (payload vacío, o un <c>data</c> con solo
    /// <c>timeStamp</c> y sin <c>average</c> — Azure Monitor puede omitir el campo para
    /// intervalos sin datos), o algún elemento de <c>value</c> reportó un <c>errorCode</c>
    /// distinto de <c>"Success"</c>. Ambos casos son indistinguibles de "0 GiB" si se tratan
    /// como cero, y pueden esconder capacidad facturable real. El llamador (<see cref="EnrichAsync"/>)
    /// trata null exactamente igual que una excepción: advertencia visible, nunca cero silencioso.
    ///
    /// Suma el último punto de CADA timeseries (en vez de quedarse con el de la última serie
    /// vista, como antes): esta métrica siempre trae una sola serie "&lt;All&gt;" en la práctica
    /// verificada, pero sumar es la lectura correcta si Azure alguna vez devolviera más de una
    /// serie — evita que el total de la cuenta se reduzca silenciosamente al valor de una sola
    /// serie.
    /// </summary>
    private static async Task<double?> GetSnapshotDifferentialGibAsync(
        HttpClient http, string bearerToken, string accountId, CancellationToken ct)
    {
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-SnapshotMetricLookbackDays);
        var timespan = $"{start:yyyy-MM-ddTHH:mm:ssZ}/{end:yyyy-MM-ddTHH:mm:ssZ}";
        var url = $"https://management.azure.com{accountId}/fileServices/default/providers/microsoft.insights/metrics"
            + $"?api-version={MetricsApiVersion}&metricnames=FileShareSnapshotSize&aggregation=Average"
            + $"&timespan={Uri.EscapeDataString(timespan)}&interval=P1D";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var metrics) || metrics.ValueKind != JsonValueKind.Array)
        {
            return null; // payload sin "value": no hay nada que sumar, no se puede confiar en "0 GiB".
        }

        double sumOfLastPerSeries = 0d;
        var foundAnyPoint = false;
        foreach (var metric in metrics.EnumerateArray())
        {
            if (metric.TryGetProperty("errorCode", out var errorCode)
                && errorCode.ValueKind == JsonValueKind.String
                && !string.Equals(errorCode.GetString(), "Success", StringComparison.Ordinal))
            {
                // Azure reportó un error explícito para esta métrica (ej. "InternalServerError",
                // "ThrottledRequests"): la ausencia de datos NO significa "0 GiB".
                return null;
            }
            if (!metric.TryGetProperty("timeseries", out var series) || series.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var ts in series.EnumerateArray())
            {
                if (!ts.TryGetProperty("data", out var points) || points.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                // Los puntos vienen en orden cronológico ascendente (verificado empíricamente);
                // nos quedamos con el ÚLTIMO valor no nulo DE ESTA SERIE (el más reciente
                // disponible) y sumamos entre series — ver nota de la clase sobre por qué sumar.
                double? lastOfThisSeries = null;
                foreach (var point in points.EnumerateArray())
                {
                    if (point.TryGetProperty("average", out var avg) && avg.ValueKind == JsonValueKind.Number)
                    {
                        lastOfThisSeries = avg.GetDouble();
                        foundAnyPoint = true;
                    }
                }
                sumOfLastPerSeries += lastOfThisSeries ?? 0d;
            }
        }

        if (!foundAnyPoint)
        {
            // Ningún punto de ninguna serie trajo "average" (ej. todos los "data" solo tienen
            // "timeStamp"): no hay lectura real, no se puede confiar en "0 GiB".
            return null;
        }
        return sumOfLastPerSeries / BytesPerGib;
    }

    /// <summary>Body real verificado: <c>{"error":{"code":"FeatureNotSupportedForAccount",...}}</c>.
    /// Cualquier otro código (o body no parseable) devuelve false, para que el llamador siga
    /// lanzando la excepción HTTP estándar (y por lo tanto la advertencia de siempre).</summary>
    private static bool IsFeatureNotSupportedForAccount(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && code.GetString() == FeatureNotSupportedErrorCode;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Señal interna: la cuenta no tiene servicio de Files habilitado (400
    /// FeatureNotSupportedForAccount). Se distingue de una falla real para que
    /// <see cref="EnrichAsync"/> la trate como "0 shares" (omitir en silencio) en vez de
    /// advertencia.</summary>
    private sealed class FileServiceNotSupportedException(string message) : Exception(message);
}
