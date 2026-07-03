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
        var started = _byAnalysis.TryGetValue(analysisId, out var s) ? s.StartedAt : Now;
        _byAnalysis[analysisId] = new PowerHistoryJobStatus("completed", started, Now, summaryJson, null);
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(int analysisId, string error, CancellationToken ct)
    {
        var started = _byAnalysis.TryGetValue(analysisId, out var s) ? s.StartedAt : Now;
        _byAnalysis[analysisId] = new PowerHistoryJobStatus("failed", started, Now, null, error);
        return Task.CompletedTask;
    }

    public Task<PowerHistoryJobStatus?> GetStatusAsync(int analysisId, CancellationToken ct) =>
        Task.FromResult(_byAnalysis.TryGetValue(analysisId, out var s) ? s : null);

    public Task<bool> IsRunningAsync(int analysisId, CancellationToken ct) =>
        Task.FromResult(_byAnalysis.TryGetValue(analysisId, out var s) && s.Status == "running");
}
