using Azure.Core;
using OptimizacionCostos.Api.Features.AzureIntegration;
using OptimizacionCostos.Api.Features.Inventory;
using OptimizacionCostos.Api.Features.Reports;

namespace OptimizacionCostos.Api.Features.Optimization;

/// <summary>
/// Paso de right-sizing del barrido: lista las VMs ENCENDIDAS de una suscripción (Resource Graph),
/// consulta sus métricas de CPU de los últimos 30 días (reusa <see cref="IReportMetrics"/>) y marca
/// las subutilizadas. Requiere el rol Monitoring Reader; si faltan métricas, esa VM se omite
/// (best-effort, nunca aborta el barrido).
/// </summary>
public interface IRightSizingAnalyzer
{
    Task<IReadOnlyList<Finding>> AnalyzeAsync(TokenCredential credential, string subscriptionId, CancellationToken ct = default);
}

public sealed class RightSizingAnalyzer(
    IResourceGraphRunner rg, IReportMetrics metrics, ICostEstimation cost, ILogger<RightSizingAnalyzer> logger)
    : IRightSizingAnalyzer
{
    /// <summary>Tope de VMs a las que se les piden métricas por suscripción (una llamada REST c/u).</summary>
    private const int MaxVms = 200;

    private const string RunningVmsKql = """
        Resources
        | where type =~ 'microsoft.compute/virtualmachines'
        | extend powerState = tostring(properties.extended.instanceView.powerState.code)
        | where powerState == 'PowerState/running'
        | project id, name, location, vmSize = tostring(properties.hardwareProfile.vmSize), osType = tostring(properties.storageProfile.osDisk.osType)
        """;

    public async Task<IReadOnlyList<Finding>> AnalyzeAsync(TokenCredential credential, string subscriptionId, CancellationToken ct = default)
    {
        var nodes = await rg.RunQueryAsync(credential, [subscriptionId], RunningVmsKql, ct);
        var vms = nodes.Select(n => new RgRow(n)).ToList();
        if (vms.Count == 0) return [];

        var scope = vms;
        if (vms.Count > MaxVms)
        {
            logger.LogWarning("right-sizing sub={Sub}: {Total} VMs encendidas; se analizan las primeras {Cap}", subscriptionId, vms.Count, MaxVms);
            scope = vms.Take(MaxVms).ToList();
        }

        var targets = scope
            .Select(r => new MetricTarget(r.Str("id") ?? "", r.Str("name"), "microsoft.compute/virtualmachines", null))
            .ToList();

        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-RightSizing.WindowDays);
        var result = await metrics.ExtractMetricsAsync(credential, targets, start, end, ct);

        // Índice CPU avg/max por nombre de recurso (VM names únicos por suscripción en la práctica).
        var byName = new Dictionary<string, (double? Avg, double? Max)>(StringComparer.OrdinalIgnoreCase);
        if (result.GetValueOrDefault("virtual_machines") is IEnumerable<Dictionary<string, object?>> vmMetrics)
            foreach (var e in vmMetrics)
            {
                if (e.GetValueOrDefault("resource_name") is not string nm || nm.Length == 0) continue;
                byName[nm] = (e.GetValueOrDefault("cpu_avg") as double?, e.GetValueOrDefault("cpu_max") as double?);
            }

        var findings = new List<Finding>();
        foreach (var r in scope)
        {
            var name = r.Str("name") ?? "";
            if (!byName.TryGetValue(name, out var m)) continue; // sin métricas (403/no disponible) → omitir
            var f = RightSizing.TryBuild(subscriptionId, r.Str("id") ?? "", name, r.Str("location") ?? "",
                r.Str("vmSize") ?? "", r.Str("osType") ?? "Linux", m.Avg, m.Max, cost);
            if (f is not null) findings.Add(f);
        }
        return findings;
    }
}
