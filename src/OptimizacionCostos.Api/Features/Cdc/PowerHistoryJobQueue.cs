using System.Threading.Channels;

namespace OptimizacionCostos.Api.Features.Cdc;

/// <summary>Trabajo de refresh de encendido/apagado encolado por el controller.</summary>
public sealed record PowerHistoryJob(int AnalysisId, string? Actor);

/// <summary>
/// Cola en proceso para el refresh de power-history. El POST .../power-history/refresh responde 202
/// y encola; un BackgroundService drena la cola. Mismo patrón que IReportJobQueue (en proceso,
/// best-effort, se pierde si el worker reinicia).
/// </summary>
public interface IPowerHistoryJobQueue
{
    void Enqueue(PowerHistoryJob job);
    ValueTask<PowerHistoryJob> DequeueAsync(CancellationToken ct);
}

public sealed class PowerHistoryJobQueue : IPowerHistoryJobQueue
{
    private readonly Channel<PowerHistoryJob> _channel = Channel.CreateUnbounded<PowerHistoryJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public void Enqueue(PowerHistoryJob job) => _channel.Writer.TryWrite(job);
    public ValueTask<PowerHistoryJob> DequeueAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}
