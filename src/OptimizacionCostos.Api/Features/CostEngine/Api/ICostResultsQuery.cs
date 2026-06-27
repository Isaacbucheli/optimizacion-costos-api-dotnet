namespace OptimizacionCostos.Api.Features.CostEngine.Api;

/// <summary>
/// Consultas de lectura/escritura puntual del router de costos que NO viven en el motor
/// ni en ScenarioService: el SELECT grande de get_results, el GET de escenarios persistidos
/// y el UPDATE de costo manual. Port de las consultas inline de cost_calculation.py.
/// </summary>
public interface ICostResultsQuery
{
    /// <summary>
    /// cost_results JOIN azure_resources LEFT JOIN vm_power_usage del analisis,
    /// filtrable por service_key. Port de get_results.
    /// </summary>
    Task<IReadOnlyList<CostResultRow>> GetResultsAsync(int analysisId, string? serviceKey, CancellationToken ct = default);

    /// <summary>
    /// cost_scenarios + scenario_breakdown del analisis. Port de get_scenarios.
    /// </summary>
    Task<IReadOnlyList<ScenarioReadDto>> GetScenariosAsync(int analysisId, CancellationToken ct = default);

    /// <summary>
    /// UPDATE de costo manual. Devuelve false si el cost_result no existe (404).
    /// Port de set_manual_cost.
    /// </summary>
    Task<bool> SetManualCostAsync(int costResultId, double? manualMonthlyCost, string? manualCostNote, CancellationToken ct = default);
}
