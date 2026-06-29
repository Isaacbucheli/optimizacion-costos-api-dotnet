using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.AzureIntegration;
using OptimizacionCostos.Api.Features.Storage;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>Trabajo de generación de informe encolado por el controller (port del payload de _background_generate).</summary>
public sealed record ReportJob(int ClientId, int Year, int Month, int CredentialId, bool IncludeNarrative, string? GeneratedBy);

/// <summary>
/// Cola en proceso para la generación de informes. Reemplaza FastAPI BackgroundTasks: el POST
/// /reports/.../generate responde 202 y encola; un BackgroundService drena la cola. Misma semántica
/// (en proceso, best-effort, se pierde si el worker reinicia — igual que BackgroundTasks).
/// </summary>
public interface IReportJobQueue
{
    void Enqueue(ReportJob job);
    ValueTask<ReportJob> DequeueAsync(CancellationToken ct);
}

public sealed class ReportJobQueue : IReportJobQueue
{
    private readonly Channel<ReportJob> _channel = Channel.CreateUnbounded<ReportJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public void Enqueue(ReportJob job) => _channel.Writer.TryWrite(job);
    public ValueTask<ReportJob> DequeueAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}

/// <summary>
/// Orquesta la generación de un informe: arma la credencial, llama al builder, sube el JSON a Blob y
/// actualiza el índice (port de _background_generate de app/routes/reports.py).
/// </summary>
public interface IReportGenerationService
{
    Task RunAsync(ReportJob job, CancellationToken ct);
}

public sealed class ReportGenerationService(
    IReportBuilder builder,
    IReportStore store,
    IBlobStorageService blobs,
    IAzureCredentialFactory credentials,
    AppConfig config,
    ILogger<ReportGenerationService> logger) : IReportGenerationService
{
    // json.dumps(report, ensure_ascii=False)
    private static readonly JsonSerializerOptions DumpOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string BlobName(int clientId, int year, int month) =>
        $"reports/client-{clientId}/informe-{year}-{month:D2}.json";

    public async Task RunAsync(ReportJob job, CancellationToken ct)
    {
        try
        {
            var credential = await credentials.GetClientSecretCredentialAsync(job.CredentialId, ct);

            JsonObject report;
            try
            {
                report = await builder.BuildReportAsync(job.ClientId, job.Year, job.Month, credential, job.IncludeNarrative, ct);
            }
            catch (ReportNoSubscriptionsException ex)
            {
                await store.MarkFailedAsync(job.ClientId, job.Year, job.Month, ex.Message, ct);
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error generando informe client={Cid} {Y}-{M}", job.ClientId, job.Year, job.Month);
                await store.MarkFailedAsync(job.ClientId, job.Year, job.Month, "Error interno al generar el informe", ct);
                return;
            }

            var blobName = BlobName(job.ClientId, job.Year, job.Month);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(report.ToJsonString(DumpOpts));
                await blobs.UploadAsync(config.StorageContainerOutputs, blobName, bytes, "application/json", ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No se pudo subir el blob del informe client={Cid} {Y}-{M}", job.ClientId, job.Year, job.Month);
                await store.MarkFailedAsync(job.ClientId, job.Year, job.Month, "No se pudo guardar el informe en Storage", ct);
                return;
            }

            var isPartial = (report["period"] as JsonObject)?["partial"] is JsonValue pv && pv.TryGetValue<bool>(out var p) && p;
            var summary = Summary(report).ToJsonString(DumpOpts);
            await store.MarkCompletedAsync(job.ClientId, job.Year, job.Month, config.StorageContainerOutputs, blobName, isPartial, summary, job.GeneratedBy, ct);

            try
            {
                await credentials.WriteAuditLogAsync(job.CredentialId, "monthly_report_generated", job.GeneratedBy,
                    $"client={job.ClientId} period={job.Year}-{job.Month:D2}", ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo escribir auditoria de informe (best-effort)");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error inesperado en background generation client={Cid} {Y}-{M}", job.ClientId, job.Year, job.Month);
            try { await store.MarkFailedAsync(job.ClientId, job.Year, job.Month, "Error inesperado al generar el informe", ct); }
            catch (Exception inner) { logger.LogError(inner, "Tampoco se pudo marcar el informe como fallido"); }
        }
    }

    // Port de _summary.
    private static JsonObject Summary(JsonObject report)
    {
        var performance = report["performance"] as JsonObject ?? new JsonObject();
        var inventory = report["inventario"] as JsonArray ?? new JsonArray();
        var backups = report["backups"] as JsonObject ?? new JsonObject();
        var period = report["period"] as JsonObject ?? new JsonObject();

        var vmsRunning = inventory.OfType<JsonObject>().Count(vm => vm["status"]?.GetValue<string>() == "Running");
        var vmsWithMetrics = (performance["virtual_machines"] as JsonArray)?.Count ?? 0;
        var backupItems = (backups["items"] as JsonArray)?.Count ?? 0;
        var hasNarrative = report["narrative"] is JsonObject;

        return new JsonObject
        {
            ["period_label"] = period["label"]?.DeepClone(),
            ["partial"] = period["partial"]?.DeepClone(),
            ["vms_total"] = inventory.Count,
            ["vms_running"] = vmsRunning,
            ["vms_with_metrics"] = vmsWithMetrics,
            ["backup_items"] = backupItems,
            ["has_narrative"] = hasNarrative,
        };
    }
}

/// <summary>Drena la cola de informes: una generación a la vez, cada una en su propio scope DI.</summary>
public sealed class ReportGenerationBackgroundService(
    IReportJobQueue queue, IServiceScopeFactory scopes, ILogger<ReportGenerationBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ReportJob job;
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
                var service = scope.ServiceProvider.GetRequiredService<IReportGenerationService>();
                await service.RunAsync(job, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fallo no controlado procesando informe client={Cid} {Y}-{M}", job.ClientId, job.Year, job.Month);
            }
        }
    }
}
