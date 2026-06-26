namespace OptimizacionCostos.Api.Features.CostEngine.Engine;

/// <summary>Resumen por servicio del cálculo (equivale a summary[svc_key] del Python).</summary>
public sealed class ServiceCalculationSummary
{
    public int Calculated { get; set; }
    public int ResourcesLoaded { get; set; }
    public int ResourceOffset { get; set; }
    public int? ResourceLimit { get; set; }
    public bool HasMore { get; set; }
    public Dictionary<string, int> StatusBreakdown { get; set; } = new(StringComparer.Ordinal);
    public string? Error { get; set; }
}

/// <summary>Resultado de calculate_analysis: analysis_id + resumen por servicio.</summary>
public sealed class CalculationSummary
{
    public int AnalysisId { get; set; }
    public Dictionary<string, ServiceCalculationSummary> Summary { get; set; } = new(StringComparer.Ordinal);
    public string? Error { get; set; }
}
