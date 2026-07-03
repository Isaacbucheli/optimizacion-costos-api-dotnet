using OptimizacionCostos.Api.Features.Cdc;
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
}
