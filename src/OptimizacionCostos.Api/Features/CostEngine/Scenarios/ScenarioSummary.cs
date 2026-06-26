namespace OptimizacionCostos.Api.Features.CostEngine.Scenarios;

/// <summary>Resumen de un escenario calculado (lo que devuelve build_scenarios por escenario).</summary>
public sealed class ScenarioSummaryItem
{
    public int ScenarioId { get; set; }
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public double TotalMonthly { get; set; }
    public double TotalAnnual { get; set; }
    public double SavingsMonthly { get; set; }
    public double SavingsAnnual { get; set; }
    public double SavingsPct { get; set; }
    public IReadOnlyList<ScenarioBreakdownLine> Breakdown { get; set; } = [];
}

/// <summary>Resultado de build_scenarios.</summary>
public sealed class ScenariosResult
{
    public int AnalysisId { get; set; }
    public double PaygBaselineMonthly { get; set; }
    public double PaygBaselineAnnual { get; set; }
    public IReadOnlyList<ScenarioSummaryItem> Scenarios { get; set; } = [];
    public string? Error { get; set; }
}
