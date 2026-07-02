namespace OptimizacionCostos.Api.Features.FinOpsData;

public sealed record FinOpsRefreshStatus(string Dataset, DateTime? RefreshedAt, int? RowCount, string? Status);

public interface IFinOpsDataStore
{
    /// <summary>Reemplaza el dataset completo en su tabla (transacción DELETE+INSERT).</summary>
    Task ReplaceDatasetAsync(string datasetKey, IReadOnlyList<string[]> rows, CancellationToken ct);
    Task RecordRefreshAsync(string datasetKey, int rowCount, string status, CancellationToken ct);
    Task<IReadOnlyList<FinOpsRefreshStatus>> GetStatusAsync(CancellationToken ct);
}
