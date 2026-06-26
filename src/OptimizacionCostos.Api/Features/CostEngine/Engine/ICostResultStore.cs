namespace OptimizacionCostos.Api.Features.CostEngine.Engine;

/// <summary>Persiste/borra resultados de cálculo. Equivale a _insert/_delete de cost_engine.py.</summary>
public interface ICostResultStore
{
    void DeleteAllForAnalysis(int analysisId);
    void DeleteForService(int analysisId, string serviceKey);
    void Insert(IReadOnlyList<CostResult> results);
}
