namespace OptimizacionCostos.Api.Features.FinOpsData;

public sealed record FinOpsRefreshResult(string Dataset, string Status, int RowCount);

public interface IFinOpsDataRefreshService
{
    Task<IReadOnlyList<FinOpsRefreshResult>> RefreshAllAsync(CancellationToken ct);
    /// <summary>Refresca solo los datasets rancios/vacíos, best-effort con timeout. true si todo quedó fresco.</summary>
    Task<bool> EnsureFreshBestEffortAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Descarga los CSVs open data del FinOps Toolkit y los ingiere. Si la descarga, el header
/// o el sanity check fallan, CONSERVA los datos previos y registra error (nunca bloquea el cálculo).
/// <paramref name="minRowsOverride"/>: en tests, baja los umbrales MinRows (0 = usar los reales).
/// </summary>
public sealed class FinOpsDataRefreshService(HttpClient http, IFinOpsDataStore store, int minRowsOverride = 0)
    : IFinOpsDataRefreshService
{
    public async Task<IReadOnlyList<FinOpsRefreshResult>> RefreshAllAsync(CancellationToken ct)
    {
        var results = new List<FinOpsRefreshResult>();
        foreach (var ds in FinOpsDatasets.All)
            results.Add(await RefreshOneAsync(ds, ct));
        return results;
    }

    public async Task<bool> EnsureFreshBestEffortAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var status = (await store.GetStatusAsync(cts.Token)).ToDictionary(s => s.Dataset);
            var ok = true;
            foreach (var ds in FinOpsDatasets.All)
            {
                var fresh = status.TryGetValue(ds.Key, out var s)
                    && s.Status == "ok"
                    && s.RefreshedAt is { } at
                    && (DateTime.UtcNow - at).TotalDays <= ds.StaleDays;
                if (fresh) continue;
                var r = await RefreshOneAsync(ds, cts.Token);
                ok &= r.Status == "ok";
            }
            return ok;
        }
        catch (OperationCanceledException)
        {
            return false; // timeout: se calcula con lo que haya
        }
    }

    private async Task<FinOpsRefreshResult> RefreshOneAsync(FinOpsDataset ds, CancellationToken ct)
    {
        try
        {
            var res = await http.GetAsync(FinOpsDatasets.BaseUrl + ds.FileName, ct);
            if (!res.IsSuccessStatusCode)
                return await FailAsync(ds, $"HTTP {(int)res.StatusCode}", ct);

            var body = await res.Content.ReadAsStringAsync(ct);
            var rows = FinOpsCsv.Parse(body, out var header);

            for (var i = 0; i < ds.ExpectedHeaderStart.Length; i++)
            {
                if (header.Length <= i || !string.Equals(header[i], ds.ExpectedHeaderStart[i], StringComparison.OrdinalIgnoreCase))
                    return await FailAsync(ds, $"header inesperado: {string.Join(",", header)}", ct);
            }

            var minRows = minRowsOverride > 0 ? minRowsOverride : ds.MinRows;
            if (rows.Count < minRows)
                return await FailAsync(ds, $"sanity: {rows.Count} filas < mínimo {minRows}", ct);

            await store.ReplaceDatasetAsync(ds.Key, rows, ct);
            await store.RecordRefreshAsync(ds.Key, rows.Count, "ok", ct);
            return new FinOpsRefreshResult(ds.Key, "ok", rows.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return await FailAsync(ds, ex.GetType().Name, ct);
        }
    }

    private async Task<FinOpsRefreshResult> FailAsync(FinOpsDataset ds, string detail, CancellationToken ct)
    {
        try { await store.RecordRefreshAsync(ds.Key, 0, $"error:{detail}", ct); } catch { /* best effort */ }
        return new FinOpsRefreshResult(ds.Key, $"error:{detail}", 0);
    }
}
