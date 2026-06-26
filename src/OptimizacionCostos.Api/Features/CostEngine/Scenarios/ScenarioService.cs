namespace OptimizacionCostos.Api.Features.CostEngine.Scenarios;

/// <summary>Calcula y persiste los 4 escenarios. Port 1:1 de scenarios.py::build_scenarios.</summary>
public sealed class ScenarioService(IScenarioDataSource dataSource)
{
    public ScenariosResult BuildScenarios(int analysisId)
    {
        var results = dataSource.LoadResultsWithMetadata(analysisId);
        if (results.Count == 0)
        {
            return new ScenariosResult
            {
                AnalysisId = analysisId,
                Error = "No cost_results for this analysis. Run calculate first.",
            };
        }

        var paygBaseline = ScenarioBuilder.BuildPaygBaseline(results);
        dataSource.DeleteScenarios(analysisId);

        var summaries = new List<ScenarioSummaryItem>();
        foreach (var config in ScenarioConfig.All)
        {
            var totalMonthly = results.Sum(r => ScenarioBuilder.EffectiveCostForScenario(r, config));
            var totalAnnual = totalMonthly * 12.0;
            var savingsMonthly = paygBaseline - totalMonthly;
            var savingsAnnual = savingsMonthly * 12.0;
            var savingsPct = paygBaseline > 0 ? savingsMonthly / paygBaseline : 0.0;

            var scenarioId = dataSource.InsertScenario(
                analysisId, config,
                Math.Round(totalMonthly, 4), Math.Round(totalAnnual, 4),
                Math.Round(savingsMonthly, 4), Math.Round(savingsAnnual, 4), Math.Round(savingsPct, 6));

            var breakdown = ScenarioBuilder.BuildBreakdown(results, config);
            foreach (var line in breakdown)
            {
                dataSource.InsertBreakdown(scenarioId, line);
            }

            summaries.Add(new ScenarioSummaryItem
            {
                ScenarioId = scenarioId,
                Number = config.Number,
                Name = config.Name,
                TotalMonthly = Math.Round(totalMonthly, 2),
                TotalAnnual = Math.Round(totalAnnual, 2),
                SavingsMonthly = Math.Round(savingsMonthly, 2),
                SavingsAnnual = Math.Round(savingsAnnual, 2),
                SavingsPct = Math.Round(savingsPct, 6),
                Breakdown = breakdown,
            });
        }

        return new ScenariosResult
        {
            AnalysisId = analysisId,
            PaygBaselineMonthly = Math.Round(paygBaseline, 2),
            PaygBaselineAnnual = Math.Round(paygBaseline * 12.0, 2),
            Scenarios = summaries,
        };
    }
}
