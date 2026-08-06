using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Waf;

public sealed record WafAdvisorSyncJob(
    int JobId, int ClientId, IReadOnlyList<string> Subscriptions,
    string? Actor, int TimeoutSecondsPerSubscription);

public sealed record WafAdvisorSyncJobStatus(
    int JobId, int ClientId, string Status,
    int SubscriptionsTotal, int SubscriptionsProcessed, int SubscriptionsFailed,
    string? CurrentSubscription, int? IngestionRunId,
    int NewRecommendations, int NewFindings, int ResolvedFindings,
    string? WarningsJson, string? Error,
    DateTime StartedAt, DateTime? CompletedAt);

public sealed record WafAdvisorSyncProgress(
    int Processed, int Failed, int Total, string? CurrentSubscription);

/// <summary>Resultado de pedir una corrida: se creó, ya había una activa, o está en enfriamiento.</summary>
public enum WafAdvisorSyncOutcome { Created, AlreadyActive, InCooldown }

/// <summary>
/// <paramref name="Job"/> es la corrida creada, la activa, o la última terminada segun el caso.
/// <paramref name="RetryAfter"/> solo viene con <see cref="WafAdvisorSyncOutcome.InCooldown"/>.
/// </summary>
public sealed record WafAdvisorSyncStartResult(
    WafAdvisorSyncOutcome Outcome, WafAdvisorSyncJobStatus? Job, TimeSpan? RetryAfter);

/// <summary>
/// Decision del enfriamiento, separada del SQL a proposito: la consulta necesita base de datos y la
/// suite de CI corre sin una, asi que la aritmetica (bordes, enfriamiento apagado, reloj hacia atras)
/// se prueba aca. El SQL solo aporta los segundos transcurridos.
/// </summary>
public static class WafAdvisorSyncCooldown
{
    /// <summary>
    /// Cuanto falta para poder volver a consultar Advisor, o <c>null</c> si ya se puede.
    /// </summary>
    /// <param name="secondsSinceLastFinished">
    /// Segundos desde que termino la ultima corrida, o <c>null</c> si no hay ninguna terminada.
    /// </param>
    public static TimeSpan? Remaining(int? secondsSinceLastFinished, TimeSpan cooldown)
    {
        if (cooldown <= TimeSpan.Zero) return null;        // enfriamiento desactivado
        if (secondsSinceLastFinished is null) return null; // primera corrida del cliente

        // Se acota a >= 0: un salto de reloj hacia atras daria un transcurrido negativo y volveria el
        // enfriamiento mas largo que lo configurado, o incluso eterno.
        var transcurrido = TimeSpan.FromSeconds(Math.Max(0, secondsSinceLastFinished.Value));
        return transcurrido < cooldown ? cooldown - transcurrido : null;
    }
}

public interface IWafAdvisorSyncJobQueue
{
    void Enqueue(WafAdvisorSyncJob job);
    ValueTask<WafAdvisorSyncJob> DequeueAsync(CancellationToken ct);
}

public sealed class WafAdvisorSyncJobQueue : IWafAdvisorSyncJobQueue
{
    private readonly Channel<WafAdvisorSyncJob> _channel = Channel.CreateUnbounded<WafAdvisorSyncJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public void Enqueue(WafAdvisorSyncJob job) => _channel.Writer.TryWrite(job);
    public ValueTask<WafAdvisorSyncJob> DequeueAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}

public interface IWafAdvisorSyncJobStore
{
    /// <summary>
    /// Pide una corrida de Advisor. No crea una segunda si ya hay activa, y tampoco si la ultima
    /// termino hace menos de <paramref name="cooldown"/>. <c>TimeSpan.Zero</c> desactiva el
    /// enfriamiento.
    ///
    /// El enfriamiento vive aca y no en el controlador porque la comprobacion tiene que ser atomica
    /// con el insert: este metodo ya corre en una transaccion Serializable con UPDLOCK/HOLDLOCK, asi
    /// que dos peticiones a la vez no pueden colarse las dos. Chequearlo antes de llamar dejaria la
    /// ventana abierta justo para el patron que se quiere frenar, una rafaga de peticiones paralelas.
    /// </summary>
    Task<WafAdvisorSyncStartResult> StartAsync(
        int clientId, IReadOnlyList<string> subscriptions, string? actor,
        int timeoutSecondsPerSubscription, TimeSpan cooldown, CancellationToken ct);
    Task<WafAdvisorSyncJobStatus?> GetAsync(int clientId, int jobId, CancellationToken ct);
    Task<WafAdvisorSyncJobStatus?> GetActiveAsync(int clientId, CancellationToken ct);
    Task<IReadOnlyList<WafAdvisorSyncJob>> LoadQueuedAsync(CancellationToken ct);
    Task<int> MarkOrphanedRunningAsFailedAsync(string error, CancellationToken ct);
    Task MarkRunningAsync(int jobId, CancellationToken ct);
    Task MarkProgressAsync(int jobId, WafAdvisorSyncProgress progress, CancellationToken ct);
    Task MarkCompletedAsync(int jobId, WafAdvisorSyncResult result, CancellationToken ct);
    Task MarkFailedAsync(int jobId, string error, CancellationToken ct);
}

public sealed class SqlWafAdvisorSyncJobStore(ISqlConnectionFactory factory) : IWafAdvisorSyncJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string JobColumns = """
        job_id, client_id, status, subscriptions_total, subscriptions_processed,
        subscriptions_failed, current_subscription, ingestion_run_id, new_recommendations,
        new_findings, resolved_findings, warnings_json, error_message, started_at, completed_at
        """;

    public async Task<WafAdvisorSyncStartResult> StartAsync(
        int clientId, IReadOnlyList<string> subscriptions, string? actor,
        int timeoutSecondsPerSubscription, TimeSpan cooldown, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);

        await using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = $"""
                SELECT TOP 1 {JobColumns}
                FROM dbo.waf_advisor_sync_job WITH (UPDLOCK, HOLDLOCK)
                WHERE client_id = @cid AND status IN ('queued', 'running')
                ORDER BY job_id DESC
                """;
            find.Parameters.Add(new SqlParameter("@cid", clientId));
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var existing = Read(reader);
                await reader.DisposeAsync();
                await tx.CommitAsync(ct);
                return new WafAdvisorSyncStartResult(WafAdvisorSyncOutcome.AlreadyActive, existing, null);
            }
        }

        // Enfriamiento: el refresco de Advisor es la unica operacion que ESCRIBE en ARM del cliente
        // (dispara la generacion de recomendaciones), asi que repetirla en bucle no solo gasta
        // llamadas, toca la suscripcion ajena. La guarda de "ya hay una activa" no alcanzaba: una
        // corrida dura entre 10 y 30 segundos, asi que peticiones espaciadas la esquivan siempre. Es
        // exactamente lo que paso el 2026-08-03, seis corridas reales en cuarenta minutos.
        if (cooldown > TimeSpan.Zero)
        {
            await using var ultima = conn.CreateCommand();
            ultima.Transaction = tx;
            // El tiempo transcurrido lo calcula SQL Server y no el proceso, para no depender de que
            // los relojes del App Service y de la base esten sincronizados. Se mira status NOT IN
            // ('queued','running') en vez de listar los terminales ('completed', 'partial', 'failed')
            // para que un estado nuevo quede cubierto sin tocar esta consulta.
            ultima.CommandText = $"""
                SELECT TOP 1 {JobColumns},
                    DATEDIFF(SECOND, COALESCE(completed_at, updated_at, started_at), SYSUTCDATETIME())
                FROM dbo.waf_advisor_sync_job WITH (UPDLOCK, HOLDLOCK)
                WHERE client_id = @cid AND status NOT IN ('queued', 'running')
                ORDER BY job_id DESC
                """;
            ultima.Parameters.Add(new SqlParameter("@cid", clientId));
            await using var reader = await ultima.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var terminada = Read(reader);
                var falta = WafAdvisorSyncCooldown.Remaining(reader.GetInt32(15), cooldown);
                if (falta is not null)
                {
                    await reader.DisposeAsync();
                    await tx.CommitAsync(ct);
                    return new WafAdvisorSyncStartResult(
                        WafAdvisorSyncOutcome.InCooldown, terminada, falta);
                }
            }
        }

        var now = DateTime.UtcNow;
        int jobId;
        await using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO dbo.waf_advisor_sync_job
                    (client_id, status, subscriptions_json, subscriptions_total,
                     timeout_seconds_per_subscription, created_by, started_at, updated_at)
                OUTPUT INSERTED.job_id
                VALUES (@cid, 'queued', @subs, @total, @timeout, @by, @now, @now)
                """;
            insert.Parameters.Add(new SqlParameter("@cid", clientId));
            insert.Parameters.Add(new SqlParameter("@subs", JsonSerializer.Serialize(subscriptions, JsonOptions)));
            insert.Parameters.Add(new SqlParameter("@total", subscriptions.Count));
            insert.Parameters.Add(new SqlParameter("@timeout", timeoutSecondsPerSubscription));
            insert.Parameters.Add(new SqlParameter("@by", (object?)actor ?? DBNull.Value));
            insert.Parameters.Add(new SqlParameter("@now", now));
            jobId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct));
        }
        await tx.CommitAsync(ct);
        return new WafAdvisorSyncStartResult(
            WafAdvisorSyncOutcome.Created,
            new WafAdvisorSyncJobStatus(jobId, clientId, "queued", subscriptions.Count, 0, 0,
                null, null, 0, 0, 0, null, null, now, null),
            null);
    }

    public async Task<WafAdvisorSyncJobStatus?> GetAsync(int clientId, int jobId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT job_id, client_id, status, subscriptions_total, subscriptions_processed,
                subscriptions_failed, current_subscription, ingestion_run_id, new_recommendations,
                new_findings, resolved_findings, warnings_json, error_message, started_at, completed_at
            FROM dbo.waf_advisor_sync_job WHERE client_id = @cid AND job_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@id", jobId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<WafAdvisorSyncJobStatus?> GetActiveAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TOP 1 job_id, client_id, status, subscriptions_total, subscriptions_processed,
                subscriptions_failed, current_subscription, ingestion_run_id, new_recommendations,
                new_findings, resolved_findings, warnings_json, error_message, started_at, completed_at
            FROM dbo.waf_advisor_sync_job
            WHERE client_id = @cid AND status IN ('queued', 'running') ORDER BY job_id DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<WafAdvisorSyncJob>> LoadQueuedAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT job_id, client_id, subscriptions_json, created_by, timeout_seconds_per_subscription
            FROM dbo.waf_advisor_sync_job WHERE status = 'queued' ORDER BY job_id
            """;
        var jobs = new List<WafAdvisorSyncJob>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var subscriptions = JsonSerializer.Deserialize<List<string>>(reader.GetString(2), JsonOptions) ?? [];
            jobs.Add(new WafAdvisorSyncJob(reader.GetInt32(0), reader.GetInt32(1), subscriptions,
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4)));
        }
        return jobs;
    }

    public Task MarkRunningAsync(int jobId, CancellationToken ct) =>
        ExecuteAsync("UPDATE dbo.waf_advisor_sync_job SET status='running', updated_at=SYSUTCDATETIME() WHERE job_id=@id AND status='queued'", jobId, ct);

    public async Task MarkProgressAsync(int jobId, WafAdvisorSyncProgress progress, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.waf_advisor_sync_job SET subscriptions_processed=@processed,
                subscriptions_failed=@failed, subscriptions_total=@total,
                current_subscription=@current, updated_at=SYSUTCDATETIME()
            WHERE job_id=@id AND status='running'
            """;
        cmd.Parameters.Add(new SqlParameter("@processed", progress.Processed));
        cmd.Parameters.Add(new SqlParameter("@failed", progress.Failed));
        cmd.Parameters.Add(new SqlParameter("@total", progress.Total));
        cmd.Parameters.Add(new SqlParameter("@current", (object?)progress.CurrentSubscription ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@id", jobId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkCompletedAsync(int jobId, WafAdvisorSyncResult result, CancellationToken ct)
    {
        var terminalStatus = result.Status == "failed"
            ? "failed"
            : result.SubscriptionsFailed > 0 ? "partial" : "completed";
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.waf_advisor_sync_job SET status=@status, subscriptions_processed=@processed,
                subscriptions_failed=@failed, current_subscription=NULL, ingestion_run_id=@run,
                new_recommendations=@nr, new_findings=@nf, resolved_findings=@rf,
                warnings_json=@warnings, error_message=@error, completed_at=SYSUTCDATETIME(),
                updated_at=SYSUTCDATETIME() WHERE job_id=@id
            """;
        cmd.Parameters.Add(new SqlParameter("@status", terminalStatus));
        cmd.Parameters.Add(new SqlParameter("@processed", result.SubscriptionsProcessed));
        cmd.Parameters.Add(new SqlParameter("@failed", result.SubscriptionsFailed));
        cmd.Parameters.Add(new SqlParameter("@run", result.RunId > 0 ? result.RunId : DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@nr", result.NewRecommendations));
        cmd.Parameters.Add(new SqlParameter("@nf", result.NewFindings));
        cmd.Parameters.Add(new SqlParameter("@rf", result.ResolvedFindings));
        cmd.Parameters.Add(new SqlParameter("@warnings", result.Warnings.Count > 0
            ? JsonSerializer.Serialize(result.Warnings, JsonOptions) : DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@error", terminalStatus == "failed"
            ? "Azure Advisor no pudo procesar ninguna suscripcion." : DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@id", jobId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(int jobId, string error, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.waf_advisor_sync_job SET status='failed', current_subscription=NULL,
                error_message=@error, completed_at=SYSUTCDATETIME(), updated_at=SYSUTCDATETIME()
            WHERE job_id=@id
            """;
        cmd.Parameters.Add(new SqlParameter("@error", error.Length <= 2000 ? error : error[..2000]));
        cmd.Parameters.Add(new SqlParameter("@id", jobId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> MarkOrphanedRunningAsFailedAsync(string error, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.waf_advisor_sync_job SET status='failed', error_message=@error,
                completed_at=SYSUTCDATETIME(), updated_at=SYSUTCDATETIME()
            WHERE status='running'
            """;
        cmd.Parameters.Add(new SqlParameter("@error", error));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task ExecuteAsync(string sql, int jobId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@id", jobId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static WafAdvisorSyncJobStatus Read(SqlDataReader r) => new(
        r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetInt32(7),
        r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetString(12), r.GetDateTime(13), r.IsDBNull(14) ? null : r.GetDateTime(14));

    private static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.waf_advisor_sync_job', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.waf_advisor_sync_job (
                    job_id INT IDENTITY(1,1) PRIMARY KEY,
                    client_id INT NOT NULL,
                    status NVARCHAR(20) NOT NULL,
                    subscriptions_json NVARCHAR(MAX) NOT NULL,
                    subscriptions_total INT NOT NULL,
                    subscriptions_processed INT NOT NULL CONSTRAINT DF_waf_sync_processed DEFAULT 0,
                    subscriptions_failed INT NOT NULL CONSTRAINT DF_waf_sync_failed DEFAULT 0,
                    current_subscription NVARCHAR(255) NULL,
                    timeout_seconds_per_subscription INT NOT NULL,
                    ingestion_run_id INT NULL,
                    new_recommendations INT NOT NULL CONSTRAINT DF_waf_sync_new_rec DEFAULT 0,
                    new_findings INT NOT NULL CONSTRAINT DF_waf_sync_new_find DEFAULT 0,
                    resolved_findings INT NOT NULL CONSTRAINT DF_waf_sync_resolved DEFAULT 0,
                    warnings_json NVARCHAR(MAX) NULL,
                    error_message NVARCHAR(MAX) NULL,
                    created_by NVARCHAR(255) NULL,
                    started_at DATETIME2 NOT NULL,
                    completed_at DATETIME2 NULL,
                    updated_at DATETIME2 NOT NULL,
                    CONSTRAINT FK_waf_sync_job_client FOREIGN KEY (client_id) REFERENCES dbo.clients(client_id)
                );
            END
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.waf_advisor_sync_job') AND name = 'UX_waf_sync_job_client_active')
                CREATE UNIQUE INDEX UX_waf_sync_job_client_active ON dbo.waf_advisor_sync_job(client_id)
                    WHERE status IN ('queued', 'running');
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class WafAdvisorSyncBackgroundService(
    IWafAdvisorSyncJobQueue queue, IServiceScopeFactory scopes,
    ILogger<WafAdvisorSyncBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IWafAdvisorSyncJobStore>();
            await store.MarkOrphanedRunningAsFailedAsync("Interrumpido por reinicio del servicio; puede reintentar.", stoppingToken);
            foreach (var queued in await store.LoadQueuedAsync(stoppingToken)) queue.Enqueue(queued);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Advisor sync: fallo la reconciliacion de arranque");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            WafAdvisorSyncJob job;
            try { job = await queue.DequeueAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IWafAdvisorSyncJobStore>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IWafSyncOrchestrator>();
            try
            {
                await store.MarkRunningAsync(job.JobId, stoppingToken);
                var result = await orchestrator.RunAdvisorSyncAsync(
                    job.ClientId, job.Subscriptions, job.Actor, job.TimeoutSecondsPerSubscription,
                    (progress, ct) => store.MarkProgressAsync(job.JobId, progress, ct), stoppingToken);
                await store.MarkCompletedAsync(job.JobId, result, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Advisor sync job fallo job_id={JobId} client_id={ClientId}", job.JobId, job.ClientId);
                try { await store.MarkFailedAsync(job.JobId, $"Error interno: {ex.GetType().Name}", stoppingToken); }
                catch (Exception persistEx) { logger.LogError(persistEx, "No se pudo persistir el fallo Advisor job_id={JobId}", job.JobId); }
            }
        }
    }
}
