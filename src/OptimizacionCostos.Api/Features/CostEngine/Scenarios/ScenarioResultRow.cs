namespace OptimizacionCostos.Api.Features.CostEngine.Scenarios;

/// <summary>
/// Fila de cost_results enriquecida con metadata para evaluar escenarios.
/// Port del dict de _load_results_with_metadata (scenarios.py).
/// </summary>
public sealed record ScenarioResultRow
{
    public int CostResultId { get; init; }
    public int ResourceId { get; init; }
    public string? ServiceKey { get; init; }
    public double? PaygMonthly { get; init; }
    public double? Ri1yMonthly { get; init; }
    public double? Ri3yMonthly { get; init; }
    public bool RiApplies { get; init; }
    public string? RiCoverage { get; init; }
    public string? CalculationStatus { get; init; }
    public bool IsManualCost { get; init; }
    public double? ManualMonthlyCost { get; init; }
    public string? CalculationNotes { get; init; }
    public string? ResourceName { get; init; }
    public string? ResourceType { get; init; }
    public string? DiskCategory { get; init; }
    public string? AttachedVmName { get; init; }
    public bool? IpIsOrphan { get; init; }
    public string? PowerState { get; init; }
}
