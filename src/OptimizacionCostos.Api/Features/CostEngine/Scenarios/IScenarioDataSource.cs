namespace OptimizacionCostos.Api.Features.CostEngine.Scenarios;

/// <summary>Acceso a datos para los escenarios. Equivale a las consultas de scenarios.py.</summary>
public interface IScenarioDataSource
{
    /// <summary>cost_results JOIN metadata (disk_category, ip is_orphan, power_state).</summary>
    IReadOnlyList<ScenarioResultRow> LoadResultsWithMetadata(int analysisId);

    void DeleteScenarios(int analysisId);

    /// <summary>Inserta un escenario y devuelve su scenario_id.</summary>
    int InsertScenario(
        int analysisId, ScenarioConfig config,
        double totalMonthly, double totalAnnual,
        double savingsMonthly, double savingsAnnual, double savingsPct);

    void InsertBreakdown(int scenarioId, ScenarioBreakdownLine line);
}
