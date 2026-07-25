namespace OptimizacionCostos.Api.Features.CostEngine.Scenarios;

/// <summary>
/// Lógica pura de cálculo de escenarios. Port 1:1 de _effective_cost_for_scenario,
/// _build_breakdown y _build_payg_baseline de scenarios.py.
/// </summary>
public static class ScenarioBuilder
{
    private static readonly IReadOnlyDictionary<string, string> LabelMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vms"] = "VMs",
            ["disks"] = "Discos",
            ["sql"] = "SQL Database",
            ["appservice"] = "App Service",
            ["mysql"] = "MySQL Flex",
            ["cosmos"] = "Cosmos DB",
            ["redis"] = "Redis Cache",
            ["public_ip"] = "IPs Públicas",
            ["snapshots"] = "Snapshots de discos",
            ["storage_files"] = "Storage (Azure Files)",
        };

    /// <summary>Costo efectivo de un recurso bajo un escenario.</summary>
    public static double EffectiveCostForScenario(ScenarioResultRow r, ScenarioConfig config)
    {
        // Costo manual override; si no, payg_monthly (o 0).
        double @base = r.IsManualCost && r.ManualMonthlyCost is not null
            ? r.ManualMonthlyCost.Value
            : r.PaygMonthly ?? 0.0;

        var serviceKey = r.ServiceKey;
        var notes = r.CalculationNotes ?? "";
        var diskCategory = r.DiskCategory;
        if (serviceKey == "disks")
        {
            if (notes.Contains("category=attached_stopped", StringComparison.Ordinal))
                diskCategory = "attached_stopped";
            else if (notes.Contains("category=orphan", StringComparison.Ordinal))
                diskCategory = "orphan";
            else if (diskCategory == "unattached")
                diskCategory = "orphan";
        }

        // Depuraciones de disco (ASR nunca se elimina → devuelve base).
        if (serviceKey == "disks")
        {
            if (config.EliminateStoppedVmDisks && diskCategory == "attached_stopped")
                return 0.0;
            if (config.EliminateOrphanDisks && diskCategory == "orphan")
                return 0.0;
            return @base;
        }

        if (serviceKey == "public_ip" && config.EliminateOrphanIps)
        {
            if (r.IpIsOrphan == true)
                return 0.0;
        }

        // RI override: no aplica si el recurso YA está reservado (ri_coverage == "confirmed").
        if (r.RiApplies && r.RiCoverage != "confirmed")
        {
            if (config.UseRi1y && r.Ri1yMonthly is not null)
                return r.Ri1yMonthly.Value;
            if (config.UseRi3y && r.Ri3yMonthly is not null)
                return r.Ri3yMonthly.Value;
        }

        return @base;
    }

    /// <summary>Baseline PAYG: suma de manual_monthly_cost (si manual) o payg_monthly.</summary>
    public static double BuildPaygBaseline(IReadOnlyList<ScenarioResultRow> results)
    {
        double total = 0.0;
        foreach (var r in results)
        {
            if (r.IsManualCost && r.ManualMonthlyCost is not null)
                total += r.ManualMonthlyCost.Value;
            else if (r.PaygMonthly is not null)
                total += r.PaygMonthly.Value;
        }
        return total;
    }

    /// <summary>Líneas de breakdown por servicio (monto redondeado a 4 decimales).</summary>
    public static IReadOnlyList<ScenarioBreakdownLine> BuildBreakdown(
        IReadOnlyList<ScenarioResultRow> results, ScenarioConfig config)
    {
        // Preserva el orden de primera aparición de cada service_key (como dict de Python 3.7+).
        var order = new List<string>();
        var byService = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            var svc = r.ServiceKey ?? "";
            if (!byService.ContainsKey(svc))
            {
                order.Add(svc);
                byService[svc] = 0.0;
            }
            byService[svc] += EffectiveCostForScenario(r, config);
        }

        return order.Select(svc => new ScenarioBreakdownLine(
            ServiceKey: svc,
            LineLabel: LabelMap.GetValueOrDefault(svc, svc),
            MonthlyCost: Math.Round(byService[svc], 4))).ToList();
    }
}

/// <summary>Línea del breakdown por servicio.</summary>
public sealed record ScenarioBreakdownLine(string ServiceKey, string LineLabel, double MonthlyCost);
