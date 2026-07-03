using System.Collections.Concurrent;
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
        Assert.Equal("Error interno: InvalidOperationException", status.Error);
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

    [Fact]
    public async Task Store_MarkOrphanedRunning_MarcaSoloRunning()
    {
        var store = new FakePowerHistoryJobStore();
        await store.MarkRunningAsync(1, "x", CancellationToken.None);
        await store.MarkRunningAsync(2, "x", CancellationToken.None);
        await store.MarkCompletedAsync(2, "{}", CancellationToken.None); // completed no se toca
        var n = await store.MarkOrphanedRunningAsFailedAsync("reinicio", CancellationToken.None);
        Assert.Equal(1, n);
        Assert.Equal("failed", (await store.GetStatusAsync(1, CancellationToken.None))!.Status);
        Assert.Equal("completed", (await store.GetStatusAsync(2, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Background_AlArrancar_MarcaRunningHuerfanoComoFailed()
    {
        var queue = new PowerHistoryJobQueue();
        var store = new FakePowerHistoryJobStore();
        var svc = new FakePowerHistoryService();
        await store.MarkRunningAsync(55, "x", CancellationToken.None); // running sin job en cola = huérfano
        var bg = new PowerHistoryBackgroundService(queue, ScopeFactoryWith(svc, store), NullLogger<PowerHistoryBackgroundService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await bg.StartAsync(cts.Token);
        for (var i = 0; i < 200 && store.Peek(55)?.Status == "running"; i++)
            await Task.Delay(10, cts.Token);
        await bg.StopAsync(CancellationToken.None);

        Assert.Equal("failed", store.Peek(55)!.Status);
    }

    [Fact]
    public async Task Gather_AcumulaPorVm_YRegistraErroresBestEffort()
    {
        var units = new[] { "subA", "subB", "subC" };
        Task<IReadOnlyList<PowerHistoryService.PowerEvent>> Fetch(string u, CancellationToken _)
        {
            if (u == "subB") throw new InvalidOperationException("403");
            IReadOnlyList<PowerHistoryService.PowerEvent> evs =
            [
                new("/vm/known", "start", new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero), null),
                new("/vm/other", "start", new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero), null),
            ];
            return Task.FromResult(evs);
        }

        var (eventsByVm, errors) = await PowerHistoryService.GatherConcurrentlyAsync(
            units, Fetch, u => new { sub = u }, arm => arm == "/vm/known", 5, CancellationToken.None);

        // Solo la VM conocida se acumula; subA y subC aportan 1 evento cada una = 2.
        Assert.True(eventsByVm.ContainsKey("/vm/known"));
        Assert.Equal(2, eventsByVm["/vm/known"].Count);
        Assert.False(eventsByVm.ContainsKey("/vm/other"));
        // subB falló → 1 error, sin abortar el resto.
        Assert.Single(errors);
    }

    [Fact]
    public async Task Gather_RespetaTopeDeConcurrencia()
    {
        var units = Enumerable.Range(0, 20).Select(i => i.ToString()).ToArray();
        var current = 0;
        var maxObserved = 0;
        var gate = new object();
        async Task<IReadOnlyList<PowerHistoryService.PowerEvent>> Fetch(string u, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref current);
            lock (gate) maxObserved = Math.Max(maxObserved, now);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref current);
            return Array.Empty<PowerHistoryService.PowerEvent>();
        }

        await PowerHistoryService.GatherConcurrentlyAsync(
            units, Fetch, u => new { sub = u }, _ => false, 5, CancellationToken.None);

        Assert.True(maxObserved <= 5, $"concurrencia observada {maxObserved} > 5");
        Assert.True(maxObserved >= 2, "no hubo paralelismo real");
    }
}
