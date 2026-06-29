using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Port 1:1 de app/reports/alerts.py. REST sobre ARM (Alerts Management API), token ARM construido
/// por el llamador (se recibe ya la TokenCredential).
///
/// Convención de nombres BIT: 'BIT-{CLIENTE}-Alerta {TIPO}-{SUSCRIPCION}'. Solo se incluyen alertas
/// cuya regla comienza por 'BIT'. El tipo se extrae del nombre de la regla.
/// </summary>
public sealed partial class ReportAlerts(IHttpClientFactory httpFactory, ILogger<ReportAlerts> logger) : IReportAlerts
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ArmBase = "https://management.azure.com";
    private const string AlertsApiVersion = "2019-05-05-preview";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private const int RetentionDays = 30;

    private const string ErrorOutOfRetention =
        "La API de alertas de Azure retiene solo los ultimos 30 dias; " +
        "el periodo del informe quedo fuera de esa ventana. " +
        "Genera el informe al cierre del mes para incluir las alertas.";
    private const string ErrorQueryFailed =
        "No se pudieron consultar las alertas del periodo en Azure. " +
        "Verifica el acceso de la credencial y vuelve a generar el informe.";

    private const string BitRulePrefix = "BIT";

    [GeneratedRegex(@"-Alerta\s+(.+?)\s*(?:-|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TipoRegex();

    [GeneratedRegex(@"/subscriptions/([0-9a-f-]{36})")]
    private static partial Regex SubscriptionRegex();

    [GeneratedRegex(@"\.\d+")]
    private static partial Regex FractionRegex();

    private static readonly IReadOnlyDictionary<string, int> SeverityOrder = new Dictionary<string, int>
    {
        ["Sev0"] = 0, ["Sev1"] = 1, ["Sev2"] = 2, ["Sev3"] = 3, ["Sev4"] = 4,
    };
    private static readonly IReadOnlyDictionary<string, string> SeverityLabels = new Dictionary<string, string>
    {
        ["Sev0"] = "Critica", ["Sev1"] = "Error", ["Sev2"] = "Advertencia",
        ["Sev3"] = "Informativa", ["Sev4"] = "Detallada",
    };

    // -------------------- lógica pura (testeable) --------------------

    /// <summary>Una alerta esencial proyectada (port de los dicts de 'essentials').</summary>
    internal sealed record AlertEssential(string? AlertRule, string? Severity, string? SubscriptionId);

    /// <summary>Port de _is_bit_rule.</summary>
    internal static bool IsBitRule(string? ruleName) =>
        (ruleName ?? "").Trim().ToUpperInvariant().StartsWith(BitRulePrefix, StringComparison.Ordinal);

    /// <summary>Port de _alert_tipo: extrae el tipo del nombre de la regla.</summary>
    internal static string AlertTipo(string? ruleName)
    {
        var match = TipoRegex().Match(ruleName ?? "");
        if (match.Success)
            return match.Groups[1].Value.Trim();
        var name = (ruleName ?? "").Trim();
        if (name.ToUpperInvariant().StartsWith("BIT-", StringComparison.Ordinal))
            name = name[4..].Trim();
        return string.IsNullOrEmpty(name) ? "-" : name;
    }

    /// <summary>Port de _worst_severity: la severidad más crítica (menor índice).</summary>
    internal static string WorstSeverity(string? a, string? b)
    {
        var sa = SeverityOrder.GetValueOrDefault(a ?? "", 99);
        var sb = SeverityOrder.GetValueOrDefault(b ?? "", 99);
        var chosen = sa <= sb ? a : b;
        return chosen ?? a ?? b ?? "";
    }

    /// <summary>Saca el subscription id del id del recurso de la alerta. Port de _extract_subscription_id.</summary>
    internal static string ExtractSubscriptionId(string? alertId)
    {
        var id = (alertId ?? "").ToLowerInvariant();
        var match = SubscriptionRegex().Match(id);
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>
    /// Parsea timestamps ARM ('2026-05-20T07:11:24.0000000Z'). ARM usa fracciones de 7 dígitos que
    /// no parsean estándar: se descartan. Port de _parse_arm_timestamp.
    /// </summary>
    internal static DateTimeOffset? ParseArmTimestamp(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        var cleaned = FractionRegex().Replace(value.Trim(), "").Replace("Z", "+00:00");
        if (!DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return null;
        // DateTimeOffset siempre lleva offset; si no traía zona, TryParse asume UTC por AssumeUniversal.
        return parsed;
    }

    /// <summary>Agrega esenciales de alertas (alertRule, severity, subscriptionId). Port de _aggregate_alerts.</summary>
    internal static Dictionary<string, object?> AggregateAlerts(
        IReadOnlyList<AlertEssential> items, IReadOnlyDictionary<string, string> subNameMap)
    {
        if (items.Count == 0)
            return new Dictionary<string, object?>();

        var total = 0;
        var criticas = 0;
        var rulesSeen = new HashSet<string>();
        // Preserva el orden de inserción (igual que dict de Python) para el desempate del sort estable.
        var byTipo = new Dictionary<string, TipoAgg>();
        var byTipoOrder = new List<string>();
        var bySub = new Dictionary<string, SubAgg>();
        var bySubOrder = new List<string>();

        foreach (var item in items)
        {
            var rule = item.AlertRule ?? "-";
            var severity = item.Severity ?? "";
            var subId = item.SubscriptionId ?? "";
            var isCritical = severity is "Sev0" or "Sev1";

            total += 1;
            if (isCritical) criticas += 1;
            rulesSeen.Add(rule);

            var tipo = AlertTipo(rule);
            if (!byTipo.TryGetValue(tipo, out var tipoEntry))
            {
                tipoEntry = byTipo[tipo] = new TipoAgg { Tipo = tipo, Severidad = severity, Disparos = 0, Criticas = 0 };
                byTipoOrder.Add(tipo);
            }
            tipoEntry.Disparos += 1;
            tipoEntry.Severidad = WorstSeverity(tipoEntry.Severidad, severity);
            if (isCritical) tipoEntry.Criticas += 1;

            var subName = subNameMap.TryGetValue(subId, out var n) ? n : (string.IsNullOrEmpty(subId) ? "Desconocido" : subId);
            if (!bySub.TryGetValue(subId, out var subEntry))
            {
                subEntry = bySub[subId] = new SubAgg { Suscripcion = subName, Disparos = 0, Criticas = 0 };
                bySubOrder.Add(subId);
            }
            subEntry.Disparos += 1;
            if (isCritical) subEntry.Criticas += 1;
        }

        // sorted por -disparos, estable respecto al orden de inserción (igual que Python sorted()).
        var porTipo = byTipoOrder.Select(k => byTipo[k])
            .OrderByDescending(t => t.Disparos)
            .Select(t => new Dictionary<string, object?>
            {
                ["tipo"] = t.Tipo,
                ["severidad"] = SeverityLabels.TryGetValue(t.Severidad, out var lbl)
                    ? lbl
                    : (string.IsNullOrEmpty(t.Severidad) ? "-" : t.Severidad),
                ["disparos"] = t.Disparos,
                ["criticas"] = t.Criticas,
            })
            .ToList();

        var porSub = bySubOrder.Select(k => bySub[k])
            .OrderByDescending(s => s.Disparos)
            .Select(s => new Dictionary<string, object?>
            {
                ["suscripcion"] = s.Suscripcion,
                ["disparos"] = s.Disparos,
                ["criticas"] = s.Criticas,
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["resumen"] = new Dictionary<string, object?>
            {
                ["total"] = total,
                ["criticas"] = criticas,
                ["reglas_activadas"] = rulesSeen.Count,
                ["suscripciones"] = bySub.Count,
            },
            ["por_tipo"] = porTipo,
            ["por_suscripcion"] = porSub,
        };
    }

    private sealed class TipoAgg
    {
        public string Tipo = ""; public string Severidad = ""; public int Disparos; public int Criticas;
    }
    private sealed class SubAgg
    {
        public string Suscripcion = ""; public int Disparos; public int Criticas;
    }

    // -------------------- fetch_fired_alerts --------------------

    public async Task<IReadOnlyDictionary<string, object?>> FetchFiredAlertsAsync(
        TokenCredential credential,
        IReadOnlyList<string> subscriptions,
        IReadOnlyDictionary<string, string> subNameMap,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var retentionFloor = now.AddDays(-RetentionDays);
        var startUtc = start.ToUniversalTime();
        var endUtc = end.ToUniversalTime();

        if (endUtc <= retentionFloor)
        {
            logger.LogInformation("Periodo fuera de la retencion de 30 dias de Alerts Management");
            return new Dictionary<string, object?> { ["error"] = ErrorOutOfRetention };
        }
        var effectiveStart = startUtc > retentionFloor ? startUtc : retentionFloor;
        var partialWindow = effectiveStart > startUtc;

        string token;
        try
        {
            token = (await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct)).Token;
        }
        catch (Exception)
        {
            logger.LogWarning("No se pudo obtener token ARM para alertas");
            return new Dictionary<string, object?> { ["error"] = ErrorQueryFailed };
        }

        var essentials = new List<AlertEssential>();
        var anySuccess = false;

        var http = httpFactory.CreateClient();
        http.Timeout = RequestTimeout;

        foreach (var subId in subscriptions)
        {
            string? url = $"{ArmBase}/subscriptions/{subId}/providers/Microsoft.AlertsManagement/alerts"
                        + $"?api-version={AlertsApiVersion}&timeRange=30d&pageCount=100";
            while (url is not null)
            {
                JsonElement payload;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var resp = await http.SendAsync(req, ct);
                    resp.EnsureSuccessStatusCode();
                    payload = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement.Clone();
                }
                catch (HttpRequestException exc)
                {
                    if (exc.StatusCode is not null)
                        logger.LogWarning("No se pudieron consultar alertas de la suscripcion {Sub}: HTTP {Status}",
                            subId, (int)exc.StatusCode.Value);
                    else
                        logger.LogWarning("No se pudieron consultar alertas de la suscripcion {Sub}: {Type}",
                            subId, exc.GetType().Name);
                    break;
                }
                catch (Exception exc) when (exc is TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    logger.LogWarning("No se pudieron consultar alertas de la suscripcion {Sub}: {Type}", subId, exc.GetType().Name);
                    break;
                }

                anySuccess = true;
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("value", out var value)
                    && value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var alert in value.EnumerateArray())
                    {
                        var props = GetEssentials(alert);
                        var alertRule = Str(props, "alertRule");
                        // Solo alertas administradas/notificadas por BIT (regla 'BIT-...').
                        if (!IsBitRule(alertRule))
                            continue;
                        var firedAt = ParseArmTimestamp(Str(props, "startDateTime"));
                        // Sin fecha parseable se incluye: mejor contar de más.
                        if (firedAt is not null && !(effectiveStart <= firedAt.Value && firedAt.Value < endUtc))
                            continue;
                        var alertId = alert.ValueKind == JsonValueKind.Object && alert.TryGetProperty("id", out var idv)
                            && idv.ValueKind == JsonValueKind.String ? idv.GetString() : null;
                        var extracted = ExtractSubscriptionId(alertId);
                        essentials.Add(new AlertEssential(
                            alertRule,
                            Str(props, "severity"),
                            string.IsNullOrEmpty(extracted) ? subId : extracted));
                    }
                }
                url = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("nextLink", out var nl)
                    && nl.ValueKind == JsonValueKind.String ? nl.GetString() : null;
                if (string.IsNullOrEmpty(url)) url = null;
            }
        }

        if (!anySuccess)
        {
            logger.LogWarning("Consulta de alertas fallo en todas las suscripciones");
            return new Dictionary<string, object?> { ["error"] = ErrorQueryFailed };
        }

        logger.LogInformation("Alertas disparadas en el periodo: {Count}", essentials.Count);
        var result = AggregateAlerts(essentials, subNameMap);
        if (result.Count == 0)
        {
            // Consulta exitosa sin disparos: la sección se muestra en cero.
            result = new Dictionary<string, object?>
            {
                ["resumen"] = new Dictionary<string, object?>
                {
                    ["total"] = 0, ["criticas"] = 0, ["reglas_activadas"] = 0, ["suscripciones"] = 0,
                },
                ["por_tipo"] = new List<Dictionary<string, object?>>(),
                ["por_suscripcion"] = new List<Dictionary<string, object?>>(),
            };
        }
        if (partialWindow)
        {
            result["nota"] =
                "Ventana parcial: la API de alertas retiene 30 dias; "
                + $"se cuentan disparos desde {effectiveStart:yyyy-MM-dd}.";
        }
        return result;
    }

    /// <summary>(alert.properties.essentials) o objeto vacío. Port de la lectura anidada del Python.</summary>
    private static JsonElement GetEssentials(JsonElement alert)
    {
        if (alert.ValueKind == JsonValueKind.Object && alert.TryGetProperty("properties", out var props)
            && props.ValueKind == JsonValueKind.Object && props.TryGetProperty("essentials", out var ess)
            && ess.ValueKind == JsonValueKind.Object)
            return ess;
        return default;
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
