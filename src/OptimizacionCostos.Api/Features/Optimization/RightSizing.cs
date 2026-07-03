namespace OptimizacionCostos.Api.Features.Optimization;

/// <summary>
/// Right-sizing (Fase 2.5, Grupo B): detecta VMs subutilizadas por CPU usando métricas de Azure
/// Monitor (últimos <see cref="WindowDays"/> días). Lógica pura y testeable; la extracción de
/// métricas la hace <see cref="RightSizingAnalyzer"/> reusando el extractor del informe mensual.
/// </summary>
public static class RightSizing
{
    public const string CheckId = "underutilized_vms";
    public const int WindowDays = 30;
    /// <summary>CPU promedio bajo la cual la VM se considera candidata (Azure Advisor usa umbrales similares).</summary>
    public const double CpuAvgThreshold = 5.0;
    /// <summary>CPU máxima diaria: si nunca superó esto, hay holgura para bajar de tamaño.</summary>
    public const double CpuMaxThreshold = 20.0;

    /// <summary>Subutilizada = tiene métricas y tanto el promedio como el pico se mantuvieron bajos.</summary>
    public static bool IsUnderutilized(double? cpuAvg, double? cpuMax) =>
        cpuAvg is { } a && cpuMax is { } m && a < CpuAvgThreshold && m < CpuMaxThreshold;

    /// <summary>
    /// Construye el hallazgo de right-sizing si la VM está subutilizada; si no, null.
    /// El ahorro es orientativo (~50% del compute, cota superior conservadora).
    /// </summary>
    public static Finding? TryBuild(
        string subId, string azureResourceId, string name, string location, string vmSize, string osType,
        double? cpuAvg, double? cpuMax, ICostEstimation cost)
    {
        if (!IsUnderutilized(cpuAvg, cpuMax)) return null;
        var savings = cost.RightSizeMonthlySavings(vmSize, location, osType);
        var details = new Dictionary<string, object?>
        {
            ["vmSize"] = vmSize,
            ["cpuAvg"] = cpuAvg,
            ["cpuMax"] = cpuMax,
            ["windowDays"] = WindowDays,
            ["note"] = $"CPU baja sostenida ({WindowDays}d): promedio {cpuAvg}% / pico {cpuMax}%. Considerar bajar de tamaño. Ahorro estimado ~50% del compute (orientativo).",
        };
        return new Finding(CheckId, OptCategory.CostWaste, OptSeverity.Medium, subId, azureResourceId, name,
            "microsoft.compute/virtualmachines", location, details, savings);
    }
}
