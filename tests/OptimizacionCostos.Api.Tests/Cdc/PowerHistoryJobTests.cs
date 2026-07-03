using OptimizacionCostos.Api.Features.Cdc;
using OptimizacionCostos.Api.Tests.Cdc.Api; // FakePowerHistoryJobStore
using Xunit;

namespace OptimizacionCostos.Api.Tests.Cdc;

public sealed class PowerHistoryJobTests
{
    [Fact]
    public async Task Queue_EnqueueDequeue_DevuelveElMismoJob()
    {
        var queue = new PowerHistoryJobQueue();
        queue.Enqueue(new PowerHistoryJob(42, "isaac@bit.com"));
        var job = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(42, job.AnalysisId);
        Assert.Equal("isaac@bit.com", job.Actor);
    }

    [Fact]
    public async Task Store_MaquinaDeEstados_RunningLuegoCompleted()
    {
        var store = new FakePowerHistoryJobStore();
        var ct = CancellationToken.None;

        await store.MarkRunningAsync(7, "isaac@bit.com", ct);
        Assert.True(await store.IsRunningAsync(7, ct));
        var running = await store.GetStatusAsync(7, ct);
        Assert.Equal("running", running!.Status);
        Assert.Null(running.FinishedAt);
        Assert.Null(running.SummaryJson);
        Assert.Null(running.Error);

        await store.MarkCompletedAsync(7, "{\"updated_count\":5}", ct);
        Assert.False(await store.IsRunningAsync(7, ct));
        var done = await store.GetStatusAsync(7, ct);
        Assert.Equal("completed", done!.Status);
        Assert.NotNull(done.FinishedAt);
        Assert.Contains("updated_count", done.SummaryJson);

        await store.MarkFailedAsync(7, "boom", ct);
        var failed = await store.GetStatusAsync(7, ct);
        Assert.Equal("failed", failed!.Status);
        Assert.Equal("boom", failed.Error);
        Assert.Null(failed.SummaryJson);
    }

    [Fact]
    public async Task Store_SinRegistro_GetStatusEsNull()
    {
        var store = new FakePowerHistoryJobStore();
        Assert.Null(await store.GetStatusAsync(999, CancellationToken.None));
        Assert.False(await store.IsRunningAsync(999, CancellationToken.None));
    }
}
