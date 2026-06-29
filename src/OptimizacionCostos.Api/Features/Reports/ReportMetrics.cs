using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Port 1:1 de app/reports/metrics_extractor.py. REST sobre ARM (Azure Monitor metrics API), token
/// ARM construido por el llamador (se recibe ya la TokenCredential). Una llamada por recurso.
/// </summary>
public sealed class ReportMetrics(IHttpClientFactory httpFactory, ILogger<ReportMetrics> logger) : IReportMetrics
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ArmBase = "https://management.azure.com";
    private const string MetricsApiVersion = "2018-01-01";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Métricas por tipo de recurso. RAM de VM se reporta como % usado
    /// (100 - Available Memory Percentage), igual que el informe CDC.
    /// Port de METRIC_SPECS.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string MetricNames, string Aggregation)> MetricSpecs =
        new Dictionary<string, (string, string)>
        {
            ["microsoft.compute/virtualmachines"] = ("Percentage CPU,Available Memory Percentage", "Average,Maximum,Minimum"),
            ["microsoft.sql/managedinstances"] = ("avg_cpu_percent,storage_space_used_mb", "Average,Maximum"),
            ["microsoft.web/serverfarms"] = ("CpuPercentage,MemoryPercentage", "Average,Maximum"),
        };

    private static readonly IReadOnlyDictionary<string, string> BucketByType = new Dictionary<string, string>
    {
        ["microsoft.compute/virtualmachines"] = "virtual_machines",
        ["microsoft.sql/managedinstances"] = "sql_managed_instances",
        ["microsoft.web/serverfarms"] = "app_service_plans",
    };

    // -------------------- month_timespan --------------------

    public MonthWindow MonthTimespan(int year, int month)
    {
        var start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = month == 12
            ? new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(year, month + 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;
        var partial = end > now;
        if (partial) end = now;
        return new MonthWindow(start, end, partial);
    }

    // -------------------- extract_metrics --------------------

    public async Task<IReadOnlyDictionary<string, object?>> ExtractMetricsAsync(
        TokenCredential credential,
        IReadOnlyList<MetricTarget> targets,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default)
    {
        var token = (await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct)).Token;

        var results = new Dictionary<string, List<Dictionary<string, object?>>>
        {
            ["virtual_machines"] = [],
            ["sql_managed_instances"] = [],
            ["app_service_plans"] = [],
        };
        var errors = new List<string>();

        var http = httpFactory.CreateClient();
        http.Timeout = RequestTimeout;

        foreach (var resource in targets)
        {
            var rtype = (resource.ResourceType ?? "").ToLowerInvariant();
            if (!BucketByType.TryGetValue(rtype, out var bucket))
                continue;
            var name = resource.ResourceName ?? "";
            Dictionary<string, object?>? summary;
            try
            {
                summary = await FetchResourceMetricsAsync(http, token, resource.AzureResourceId, rtype, start, end, ct);
            }
            catch (HttpRequestException exc)
            {
                var status = exc.StatusCode;
                if (status is not null)
                {
                    logger.LogWarning("Metricas no disponibles para {Name} (HTTP {Status})", name, (int)status.Value);
                    errors.Add($"{name}: HTTP {(int)status.Value}");
                }
                else
                {
                    logger.LogWarning("Error de red consultando metricas de {Name}: {Type}", name, exc.GetType().Name);
                    errors.Add($"{name}: {exc.GetType().Name}");
                }
                continue;
            }
            catch (Exception exc) when (exc is TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Timeout u otro fallo de transporte: equivalente a httpx.HTTPError no-status.
                logger.LogWarning("Error de red consultando metricas de {Name}: {Type}", name, exc.GetType().Name);
                errors.Add($"{name}: {exc.GetType().Name}");
                continue;
            }
            if (summary is null)
                continue;
            var entry = new Dictionary<string, object?>
            {
                ["resource_name"] = name,
                ["subscription_name"] = resource.SubscriptionName,
                ["resource_group"] = resource.ResourceGroup,
            };
            foreach (var kv in summary) entry[kv.Key] = kv.Value;
            results[bucket].Add(entry);
        }

        // list.sort de Python es ESTABLE (Timsort); List<T>.Sort NO. Ante nombres duplicados,
        // OrderBy preserva el orden de inserción (orden de 'targets'), igual que Python.
        foreach (var key in results.Keys.ToList())
            results[key] = results[key]
                .OrderBy(e => ((e.GetValueOrDefault("resource_name") as string) ?? "").ToLowerInvariant(),
                         StringComparer.Ordinal)
                .ToList();

        var output = new Dictionary<string, object?>
        {
            ["virtual_machines"] = results["virtual_machines"],
            ["sql_managed_instances"] = results["sql_managed_instances"],
            ["app_service_plans"] = results["app_service_plans"],
            ["errors"] = errors,
        };
        return output;
    }

    /// <summary>
    /// Métricas diarias de un recurso. Devuelve null si el tipo no está soportado o la API no expone
    /// métricas para el recurso. Port de fetch_resource_metrics.
    /// </summary>
    private async Task<Dictionary<string, object?>?> FetchResourceMetricsAsync(
        HttpClient http, string token, string azureResourceId, string resourceType,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        if (!MetricSpecs.TryGetValue((resourceType ?? "").ToLowerInvariant(), out var spec))
            return null;
        var timespan = $"{start.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}/{end.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}";
        var query = $"?api-version={MetricsApiVersion}"
                  + $"&metricnames={Uri.EscapeDataString(spec.MetricNames)}"
                  + $"&aggregation={Uri.EscapeDataString(spec.Aggregation)}"
                  + $"&timespan={Uri.EscapeDataString(timespan)}"
                  + "&interval=P1D";
        var url = $"{ArmBase}{azureResourceId}/providers/microsoft.insights/metrics{query}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var payload = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;

        var parsed = ParseMetricPayload(payload);
        return (resourceType ?? "").ToLowerInvariant() switch
        {
            "microsoft.compute/virtualmachines" => SummarizeVm(parsed),
            "microsoft.sql/managedinstances" => SummarizeSqlMi(parsed),
            "microsoft.web/serverfarms" => SummarizeAppPlan(parsed),
            _ => null,
        };
    }

    // -------------------- lógica pura (testeable) --------------------

    /// <summary>Una métrica parseada: days + series avg/max/min (nullable por punto).</summary>
    internal sealed class ParsedMetric
    {
        public List<string> Days { get; } = [];
        public List<double?> Avg { get; } = [];
        public List<double?> Max { get; } = [];
        public List<double?> Min { get; } = [];
    }

    /// <summary>Port de _round: redondea a 2 decimales o null.</summary>
    internal static double? Round(double? value, int digits = 2) =>
        value is null ? null : Math.Round(value.Value, digits, MidpointRounding.ToEven);

    /// <summary>
    /// Convierte la respuesta de la API de métricas en {metric_name: {days, avg, max, min}}.
    /// Port de _parse_metric_payload.
    /// </summary>
    internal static Dictionary<string, ParsedMetric> ParseMetricPayload(JsonElement payload)
    {
        var parsed = new Dictionary<string, ParsedMetric>();
        if (!payload.TryGetProperty("value", out var metrics) || metrics.ValueKind != JsonValueKind.Array)
            return parsed;
        foreach (var metric in metrics.EnumerateArray())
        {
            var name = "";
            if (metric.ValueKind == JsonValueKind.Object && metric.TryGetProperty("name", out var nameObj)
                && nameObj.ValueKind == JsonValueKind.Object && nameObj.TryGetProperty("value", out var nameVal)
                && nameVal.ValueKind == JsonValueKind.String)
                name = (nameVal.GetString() ?? "").Trim();
            if (string.IsNullOrEmpty(name))
                continue;
            var pm = new ParsedMetric();
            if (metric.TryGetProperty("timeseries", out var ts) && ts.ValueKind == JsonValueKind.Array)
                foreach (var series in ts.EnumerateArray())
                {
                    if (!series.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var point in data.EnumerateArray())
                    {
                        var stamp = "";
                        if (point.TryGetProperty("timeStamp", out var tsv) && tsv.ValueKind == JsonValueKind.String)
                        {
                            var raw = tsv.GetString() ?? "";
                            stamp = raw.Length >= 10 ? raw[..10] : raw;
                        }
                        pm.Days.Add(stamp);
                        pm.Avg.Add(Round(Num(point, "average")));
                        pm.Max.Add(Round(Num(point, "maximum")));
                        pm.Min.Add(Round(Num(point, "minimum")));
                    }
                }
            parsed[name] = pm;
        }
        return parsed;
    }

    /// <summary>Port de _series_stats: avg/max/min sobre los valores no nulos, o null si vacío.</summary>
    internal static double? SeriesStats(IReadOnlyList<double?> values, string mode)
    {
        var data = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        if (data.Count == 0) return null;
        return mode switch
        {
            "avg" => Math.Round(data.Sum() / data.Count, 2, MidpointRounding.ToEven),
            "max" => Math.Round(data.Max(), 2, MidpointRounding.ToEven),
            "min" => Math.Round(data.Min(), 2, MidpointRounding.ToEven),
            _ => null,
        };
    }

    /// <summary>Port de _ram_used: 100 - disponible (% usado), preservando nulls.</summary>
    internal static List<double?> RamUsed(IReadOnlyList<double?> available) =>
        available.Select(v => v is null ? (double?)null : Math.Round(100.0 - v.Value, 2, MidpointRounding.ToEven)).ToList();

    /// <summary>Port de _summarize_vm.</summary>
    internal static Dictionary<string, object?> SummarizeVm(IReadOnlyDictionary<string, ParsedMetric> metrics)
    {
        var cpu = metrics.GetValueOrDefault("Percentage CPU") ?? new ParsedMetric();
        var mem = metrics.GetValueOrDefault("Available Memory Percentage") ?? new ParsedMetric();
        var ramAvgSeries = RamUsed(mem.Avg);
        var ramMaxSeries = RamUsed(mem.Min); // min disponible = max usado
        return new Dictionary<string, object?>
        {
            ["cpu_avg"] = SeriesStats(cpu.Avg, "avg"),
            ["cpu_max"] = SeriesStats(cpu.Max, "max"),
            ["ram_avg"] = SeriesStats(ramAvgSeries, "avg"),
            ["ram_max"] = SeriesStats(ramMaxSeries, "max"),
            ["daily"] = new Dictionary<string, object?>
            {
                ["days"] = cpu.Days,
                ["cpu_avg"] = cpu.Avg,
                ["cpu_max"] = cpu.Max,
                ["ram_avg"] = ramAvgSeries,
                ["ram_max"] = ramMaxSeries,
            },
        };
    }

    /// <summary>Port de _summarize_sql_mi.</summary>
    internal static Dictionary<string, object?> SummarizeSqlMi(IReadOnlyDictionary<string, ParsedMetric> metrics)
    {
        var cpu = metrics.GetValueOrDefault("avg_cpu_percent") ?? new ParsedMetric();
        var storage = metrics.GetValueOrDefault("storage_space_used_mb") ?? new ParsedMetric();
        var storageUsedMb = SeriesStats(storage.Max, "max");
        return new Dictionary<string, object?>
        {
            ["cpu_avg"] = SeriesStats(cpu.Avg, "avg"),
            ["cpu_max"] = SeriesStats(cpu.Max, "max"),
            ["storage_used_gb"] = storageUsedMb is null ? null : Math.Round(storageUsedMb.Value / 1024.0, 2, MidpointRounding.ToEven),
            ["daily"] = new Dictionary<string, object?>
            {
                ["days"] = cpu.Days,
                ["cpu_avg"] = cpu.Avg,
                ["cpu_max"] = cpu.Max,
            },
        };
    }

    /// <summary>Port de _summarize_app_plan.</summary>
    internal static Dictionary<string, object?> SummarizeAppPlan(IReadOnlyDictionary<string, ParsedMetric> metrics)
    {
        var cpu = metrics.GetValueOrDefault("CpuPercentage") ?? new ParsedMetric();
        var mem = metrics.GetValueOrDefault("MemoryPercentage") ?? new ParsedMetric();
        return new Dictionary<string, object?>
        {
            ["cpu_avg"] = SeriesStats(cpu.Avg, "avg"),
            ["cpu_max"] = SeriesStats(cpu.Max, "max"),
            ["ram_avg"] = SeriesStats(mem.Avg, "avg"),
            ["ram_max"] = SeriesStats(mem.Max, "max"),
            ["daily"] = new Dictionary<string, object?>
            {
                ["days"] = cpu.Days,
                ["cpu_avg"] = cpu.Avg,
                ["cpu_max"] = cpu.Max,
                ["ram_avg"] = mem.Avg,
                ["ram_max"] = mem.Max,
            },
        };
    }

    private static double? Num(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null;
}
