using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Refresco semanal del Advisor Score persistido. Port de app/waf/advisor_score_scheduler.py:
/// corre en el día/hora configurados (zona horaria), tomando un lease-lock distribuido
/// (app_job_lock) para que solo un worker ejecute. Gateado por AdvisorScoreSchedulerEnabled.
/// </summary>
public sealed class AdvisorScoreScheduledService(
    IServiceScopeFactory scopeFactory, AppConfig config, ILogger<AdvisorScoreScheduledService> logger)
    : BackgroundService
{
    private const string JobName = "advisor_score_weekly_refresh";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.AdvisorScoreSchedulerEnabled)
        {
            logger.LogInformation("Advisor Score scheduler deshabilitado");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var target = NextScheduledRun(DateTimeOffset.UtcNow);
            logger.LogInformation("Próximo refresh de Advisor Score: {Target:o}", target);
            var delay = target - DateTimeOffset.UtcNow;
            while (delay > TimeSpan.Zero && !stoppingToken.IsCancellationRequested)
            {
                var chunk = delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay;
                try { await Task.Delay(chunk, stoppingToken); }
                catch (OperationCanceledException) { return; }
                delay = target - DateTimeOffset.UtcNow;
            }
            if (stoppingToken.IsCancellationRequested) return;

            try { await RunOnceWithLockAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Advisor Score scheduled refresh falló"); }
        }
    }

    private async Task RunOnceWithLockAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAdvisorScoreStore>();
        var service = scope.ServiceProvider.GetRequiredService<IAdvisorScoreService>();

        bool acquired;
        try { acquired = await store.TryAcquireJobLockAsync(JobName, ct: ct); }
        catch (Exception ex) { logger.LogError(ex, "Advisor Score scheduler no pudo tomar el lock"); return; }

        if (!acquired)
        {
            logger.LogInformation("Advisor Score scheduler omitido: otro worker tiene el lock");
            return;
        }

        logger.LogInformation("Advisor Score weekly refresh iniciado");
        var summary = await service.RefreshAllAsync(null, "scheduler", includeInReports: true, ct);
        logger.LogInformation("Advisor Score weekly refresh terminado: {@Summary}", summary);
    }

    /// <summary>Próxima corrida (UTC) según día/hora/zona configurados. Port de next_scheduled_run.</summary>
    internal DateTimeOffset NextScheduledRun(DateTimeOffset utcNow)
    {
        var tz = ResolveTz();
        var now = TimeZoneInfo.ConvertTime(utcNow, tz);
        var (hour, minute) = ParseTime(config.AdvisorScoreSchedulerTime);
        var weekday = Math.Clamp(config.AdvisorScoreSchedulerWeekday, 0, 6); // 0=lunes (estilo Python)

        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
        var nowPyWeekday = ((int)now.DayOfWeek + 6) % 7; // .NET Sun=0 → Python Mon=0
        var daysAhead = ((weekday - nowPyWeekday) % 7 + 7) % 7;
        candidate = candidate.AddDays(daysAhead);
        if (candidate <= now) candidate = candidate.AddDays(7);
        return candidate.ToUniversalTime();
    }

    private TimeZoneInfo ResolveTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(config.AdvisorScoreSchedulerTz); }
        catch (TimeZoneNotFoundException)
        {
            logger.LogWarning("Zona horaria {Tz} no disponible, usando UTC-05:00", config.AdvisorScoreSchedulerTz);
            return TimeZoneInfo.CreateCustomTimeZone("bit-utc-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");
        }
    }

    private static (int Hour, int Minute) ParseTime(string raw)
    {
        var parts = (string.IsNullOrWhiteSpace(raw) ? "06:00" : raw).Split(':', 2);
        var hour = int.TryParse(parts[0], out var h) ? Math.Clamp(h, 0, 23) : 6;
        var minute = parts.Length > 1 && int.TryParse(parts[1], out var m) ? Math.Clamp(m, 0, 59) : 0;
        return (hour, minute);
    }
}
