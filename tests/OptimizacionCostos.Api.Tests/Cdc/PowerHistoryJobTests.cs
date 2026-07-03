using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Features.Cdc;
using OptimizacionCostos.Api.Tests.Cdc.Api; // FakePowerHistoryJobStore, FakePowerHistoryService
using Xunit;

namespace OptimizacionCostos.Api.Tests.Cdc;

public sealed class PowerHistoryJobTests
{
    private static IServiceScopeFactory ScopeFactoryWith(IPowerHistoryService svc, IPowerHistoryJobStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(svc);
        services.AddSingleton(store);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static async Task DrainUntilFinishedAsync(PowerHistoryBackgroundService svc, FakePowerHistoryJobStore store, int analysisId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        for (var i = 0; i < 200 && store.Peek(analysisId)?.FinishedAt is null; i++)
            await Task.Delay(10, cts.Token);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Background_ProcesaJob_MarcaCompletedConSummary()
    {
        var queue = new PowerHistoryJobQueue();
        var store = new FakePowerHistoryJobStore();
        var svc = new FakePowerHistoryService();
        await store.MarkRunningAsync(11, "x", CancellationToken.None); // el controller ya marca running
        queue.Enqueue(new PowerHistoryJob(11, "x"));

        var bg = new PowerHistoryBackgroundService(queue, ScopeFactoryWith(svc, store), NullLogger<PowerHistoryBackgroundService>.Instance);
        await DrainUntilFinishedAsync(bg, store, 11);

        Assert.Equal(1, svc.ComputeCalls);
        var status = store.Peek(11);
        Assert.Equal("completed", status!.Status);
        Assert.Contains("updated_count", status.SummaryJson);
    }

    [Fact]
    public async Task Background_JobQueFalla_MarcaFailed()
    {
        var queue = new PowerHistoryJobQueue();
        var store = new FakePowerHistoryJobStore();
        var svc = new FakePowerHistoryService { Throw = new InvalidOperationException("boom") };
        await store.MarkRunningAsync(22, "x", CancellationToken.None);
        queue.Enqueue(new PowerHistoryJob(22, "x"));

        var bg = new PowerHistoryBackgroundService(queue, ScopeFactoryWith(svc, store), NullLogger<PowerHistoryBackgroundService>.Instance);
        await DrainUntilFinishedAsync(bg, store, 22);

        var status = store.Peek(22);
        Assert.Equal("failed", status!.Status);
        Assert.Equal("boom", status.Error);
    }

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

    [Fact]
    public async Task Store_MarkCompletedSinRunning_EsNoOp()
    {
        var store = new FakePowerHistoryJobStore();
        await store.MarkCompletedAsync(999, "{\"updated_count\":1}", CancellationToken.None);
        Assert.Null(await store.GetStatusAsync(999, CancellationToken.None)); // sin fila previa → no-op, como UPDATE WHERE
    }
}
