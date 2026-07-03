using OptimizacionCostos.Api.Features.Cdc;

namespace OptimizacionCostos.Api.Tests.Cdc.Api;

/// <summary>
/// Doble de <see cref="IPowerHistoryJobStore"/> en memoria: implementación de referencia de la
/// máquina de estados que el store SQL debe honrar. MarkRunning limpia finished/summary/error;
/// MarkCompleted fija summary; MarkFailed fija error; IsRunning refleja status == "running".
/// </summary>
public sealed class FakePowerHistoryJobStore : IPowerHistoryJobStore
{
    private readonly Dictionary<int, PowerHistoryJobStatus> _byAnalysis = new();

    // Timestamp fijo e inyectable para tests deterministas (sin DateTimeOffset.UtcNow real).
    public DateTimeOffset Now { get; set; } = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

    public PowerHistoryJobStatus? Peek(int analysisId) => _byAnalysis.TryGetValue(analysisId, out var s) ? s : null;

    public Task MarkRunningAsync(int analysisId, string? actor, CancellationToken ct)
    {
        _byAnalysis[analysisId] = new PowerHistoryJobStatus("running", Now, null, null, null);
        return Task.CompletedTask;
    }

    public Task MarkCompletedAsync(int analysisId, string summaryJson, CancellationToken ct)
    {
        // Mirror de UPDATE ... WHERE analysis_id = @id: sin fila previa, no-op (no crea fila).
        if (_byAnalysis.TryGetValue(analysisId, out var s))
            _byAnalysis[analysisId] = new PowerHistoryJobStatus("completed", s.StartedAt, Now, summaryJson, null);
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(int analysisId, string error, CancellationToken ct)
    {
        // Mirror de UPDATE ... WHERE analysis_id = @id: sin fila previa, no-op (no crea fila).
        if (_byAnalysis.TryGetValue(analysisId, out var s))
            _byAnalysis[analysisId] = new PowerHistoryJobStatus("failed", s.StartedAt, Now, null, error);
        return Task.CompletedTask;
    }

    public Task<PowerHistoryJobStatus?> GetStatusAsync(int analysisId, CancellationToken ct) =>
        Task.FromResult(_byAnalysis.TryGetValue(analysisId, out var s) ? s : null);

    public Task<bool> IsRunningAsync(int analysisId, CancellationToken ct) =>
        Task.FromResult(_byAnalysis.TryGetValue(analysisId, out var s) && s.Status == "running");
}

/// <summary>
/// Doble de <see cref="IPowerHistoryService"/>: devuelve un summary configurable y cuenta llamadas;
/// si <see cref="Throw"/> está set, lanza (para probar el camino MarkFailed).
/// ComputeUptimeMapAsync no se usa en estos tests → devuelve mapa vacío.
/// </summary>
public sealed class FakePowerHistoryService : IPowerHistoryService
{
    public int ComputeCalls { get; private set; }
    public Exception? Throw { get; set; }
    public IReadOnlyDictionary<string, object?> Summary { get; set; } =
        new Dictionary<string, object?> { ["updated_count"] = 3, ["source"] = "activity_log" };

    public Task<IReadOnlyDictionary<string, object?>> ComputeAsync(int analysisId, CancellationToken ct = default)
    {
        ComputeCalls++;
        if (Throw is not null) throw Throw;
        return Task.FromResult(Summary);
    }

    public Task<IReadOnlyDictionary<string, (double RunningHours, double UptimePct)>> ComputeUptimeMapAsync(
        Azure.Core.TokenCredential credential, IReadOnlyList<string> subscriptions,
        IReadOnlyDictionary<string, bool> runningByArm, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, (double, double)>>(new Dictionary<string, (double, double)>());
}
