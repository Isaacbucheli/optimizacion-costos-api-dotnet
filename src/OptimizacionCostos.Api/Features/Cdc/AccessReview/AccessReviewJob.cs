using System.Threading.Channels;

namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

public sealed record AccessReviewJob(int RunId, int ClientId);

public interface IAccessReviewJobQueue
{
    void Enqueue(AccessReviewJob job);
    ValueTask<AccessReviewJob> DequeueAsync(CancellationToken ct);
}

/// <summary>Cola en memoria (best-effort). La reconciliación de arranque del worker
/// marca como error las corridas queued|running huérfanas de un reinicio.</summary>
public sealed class AccessReviewJobQueue : IAccessReviewJobQueue
{
    private readonly Channel<AccessReviewJob> _channel = Channel.CreateUnbounded<AccessReviewJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    public void Enqueue(AccessReviewJob job) => _channel.Writer.TryWrite(job);
    public ValueTask<AccessReviewJob> DequeueAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}

public sealed class AccessReviewBackgroundService(
    IAccessReviewJobQueue queue, IServiceScopeFactory scopes, ILogger<AccessReviewBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reconciliación de arranque: corridas colgadas de un reinicio del worker.
        try
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IAccessReviewStore>();
            var fixedRuns = await store.MarkOrphanedRunningAsFailedAsync(
                "Interrumpida por reinicio del servicio.", stoppingToken);
            if (fixedRuns > 0) logger.LogWarning("Access review: {N} corridas huérfanas marcadas como error", fixedRuns);
        }
        catch (Exception ex) { logger.LogError(ex, "Access review: fallo la reconciliación de arranque"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            AccessReviewJob job;
            try { job = await queue.DequeueAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IAccessReviewStore>();
            var sync = scope.ServiceProvider.GetRequiredService<IAccessReviewSyncService>();
            try
            {
                await store.MarkRunningAsync(job.RunId, stoppingToken);
                await sync.RunAsync(job.RunId, job.ClientId, stoppingToken);
                logger.LogInformation("Access review run {Run} (cliente {Client}) completada", job.RunId, job.ClientId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Access review run {Run} falló", job.RunId);
                try { await store.MarkFinishedAsync(job.RunId, "error", ex.Message, stoppingToken); }
                catch (Exception mex) { logger.LogError(mex, "Access review: no se pudo marcar error del run {Run}", job.RunId); }
            }
        }
    }
}
