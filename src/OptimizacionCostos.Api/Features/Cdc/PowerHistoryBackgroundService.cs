using System.Text.Json;

namespace OptimizacionCostos.Api.Features.Cdc;

/// <summary>
/// Drena la cola de refresh de power-history: un job a la vez, cada uno en su propio scope DI.
/// Calco de ReportGenerationBackgroundService. Usa el stoppingToken (shutdown de la app), NO el ct
/// de una request → nunca lo mata el gateway del App Service.
/// </summary>
public sealed class PowerHistoryBackgroundService(
    IPowerHistoryJobQueue queue, IServiceScopeFactory scopes, ILogger<PowerHistoryBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            PowerHistoryJob job;
            try
            {
                job = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IPowerHistoryService>();
                var store = scope.ServiceProvider.GetRequiredService<IPowerHistoryJobStore>();
                await store.MarkRunningAsync(job.AnalysisId, job.Actor, stoppingToken);
                try
                {
                    var summary = await service.ComputeAsync(job.AnalysisId, stoppingToken);
                    await store.MarkCompletedAsync(job.AnalysisId, JsonSerializer.Serialize(summary), stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Power history job falló analysis={Aid} type={Type}", job.AnalysisId, ex.GetType().Name);
                    await store.MarkFailedAsync(job.AnalysisId, ex.Message, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fallo no controlado procesando power history job analysis={Aid}", job.AnalysisId);
            }
        }
    }
}
