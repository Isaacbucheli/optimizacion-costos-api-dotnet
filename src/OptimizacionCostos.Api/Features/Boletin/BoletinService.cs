using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.AzureIntegration;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Features.Boletin;

public interface IBoletinService
{
    Task<IReadOnlyDictionary<string, object?>> RunSyncAsync(int clientId, string? actor, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, object?>> GetAsync(int clientId, CancellationToken ct = default);
}

public sealed class BoletinNoManagedSubscriptionsException()
    : Exception("El cliente no tiene suscripciones administradas activas.");

/// <summary>Sync y lectura del Boletín Azure. Patrón del módulo Optimization:
/// ARG por credencial del cliente + persistencia con reconciliación por fingerprint.</summary>
public sealed class BoletinService(
    ISqlConnectionFactory factory, IResourceGraphRunner rg, IAzureCredentialFactory credentials,
    ILogger<BoletinService> logger) : IBoletinService
{
    private static object Db(object? v) => v ?? DBNull.Value; // SqlParameter null → 8178

    public async Task<IReadOnlyDictionary<string, object?>> RunSyncAsync(int clientId, string? actor, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        var groups = await ManagedSubscriptionsAsync(conn, clientId, ct);
        if (groups.Count == 0) throw new BoletinNoManagedSubscriptionsException();

        var syncId = await CreateSyncAsync(conn, clientId, actor, ct);
        var syncStart = DateTime.UtcNow;
        try
        {
            var rows = new List<RetirementRow>();
            var healthRows = new List<RetirementRow>(); // se expanden a nivel de recurso antes de ir a "rows"
            var healthImpacted = new List<HealthImpactedResource>();
            var advisorCount = 0; var healthCount = 0; var subsScanned = 0;
            var errors = new List<object>();
            // Rastreo por fuente para la reconciliación (Finding 1): una credencial caída excluye
            // ambas fuentes de sus subs; una query fallida excluye solo esa fuente.
            var failedCredentials = new HashSet<int>();
            var advisorFailedCredentials = new HashSet<int>();
            var healthFailedCredentials = new HashSet<int>();

            foreach (var (credentialId, subIds) in groups)
            {
                Azure.Core.TokenCredential cred;
                try { cred = await credentials.GetClientSecretCredentialAsync(credentialId, ct); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "boletin sync {Sync}: credencial {Cred} no disponible", syncId, credentialId);
                    errors.Add(new { source = "credential", credential_id = credentialId, error = ex.GetType().Name });
                    failedCredentials.Add(credentialId);
                    continue;
                }

                try
                {
                    var nodes = await rg.RunQueryAsync(cred, subIds, BoletinQueries.AdvisorRetirements, ct);
                    var parsed = nodes.Select(n => BoletinParsers.FromAdvisorRow(new RgRow(n)))
                                      .Where(r => r is not null).Select(r => r!).ToList();
                    advisorCount += parsed.Count;
                    rows.AddRange(parsed);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "boletin sync {Sync}: advisor falló credencial {Cred}", syncId, credentialId);
                    errors.Add(new { source = "advisor", credential_id = credentialId, error = ex.GetType().Name });
                    advisorFailedCredentials.Add(credentialId);
                }

                try
                {
                    var nodes = await rg.RunQueryAsync(cred, subIds, BoletinQueries.ServiceHealthRetirements, ct);
                    var parsed = nodes.Select(n => BoletinParsers.FromHealthRow(new RgRow(n)))
                                      .Where(r => r is not null).Select(r => r!).ToList();
                    healthCount += parsed.Count;
                    healthRows.AddRange(parsed);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "boletin sync {Sync}: service health falló credencial {Cred}", syncId, credentialId);
                    errors.Add(new { source = "service_health", credential_id = credentialId, error = ex.GetType().Name });
                    healthFailedCredentials.Add(credentialId);
                }

                // Enriquecimiento (A1): recursos concretos impactados por los avisos de Service
                // Health. Best-effort a propósito: si falla, la fuente "health" NO se marca como
                // fallida (no entra a healthFailedCredentials) — solo se pierde el detalle de
                // recursos de esta credencial y esos avisos siguen viéndose a nivel de suscripción,
                // exactamente como antes de A1. No es la fuente de verdad de qué subs se
                // consultaron con éxito, así que tampoco debe afectar la reconciliación.
                try
                {
                    var nodes = await rg.RunQueryAsync(cred, subIds, BoletinQueries.ServiceHealthImpactedResources, ct);
                    var parsed = nodes.Select(n => BoletinParsers.FromHealthImpactedRow(new RgRow(n)))
                                      .Where(r => r is not null).Select(r => r!).ToList();
                    healthImpacted.AddRange(parsed);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "boletin sync {Sync}: recursos impactados de service health falló credencial {Cred} (enriquecimiento, no afecta la fuente health)",
                        syncId, credentialId);
                    errors.Add(new { source = "service_health_resources", credential_id = credentialId, error = ex.GetType().Name });
                }

                subsScanned += subIds.Count;
            }

            rows.AddRange(BoletinParsers.ExpandHealthRows(healthRows, healthImpacted));

            var successfulBySource = BoletinSyncPlan.SuccessfulSubscriptionsBySource(
                groups, failedCredentials, advisorFailedCredentials, healthFailedCredentials);
            var (status, errorJson) = BoletinSyncPlan.DetermineOutcome(errors);

            await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct))
            {
                foreach (var row in rows) await UpsertAsync(conn, tx, clientId, row, ct);
                foreach (var (source, subs) in successfulBySource)
                    await ReconcileAsync(conn, tx, clientId, syncStart, source, subs, ct);
                await FinalizeSyncAsync(conn, tx, syncId, subsScanned, advisorCount, healthCount, status, errorJson, ct);
                await tx.CommitAsync(ct);
            }

            return new Dictionary<string, object?>
            {
                ["sync_id"] = syncId, ["status"] = status,
                ["subscriptions_scanned"] = subsScanned,
                ["advisor_items"] = advisorCount, ["health_items"] = healthCount,
                ["errors"] = errors,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "boletin sync falló client_id={Cid}", clientId);
            try { await MarkSyncFailedAsync(conn, syncId, ex.Message, ct); } catch { /* no ocultar el error original */ }
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        var managed = await ManagedSubscriptionsAsync(conn, clientId, ct);
        var subsTotal = managed.Sum(g => g.Value.Count);
        var stored = BoletinAggregator.FilterToManaged(
            await LoadVigentesAsync(conn, clientId, ct), managed.Values.SelectMany(subs => subs));
        var subscriptionNames = await ManagedSubscriptionNamesAsync(conn, clientId, ct);
        var view = new Dictionary<string, object?>(
            BoletinAggregator.BuildView(stored, subsTotal, DateOnly.FromDateTime(DateTime.UtcNow)))
        {
            ["last_sync"] = await LoadLastSyncAsync(conn, clientId, ct),
            ["subscriptions"] = BoletinAggregator.BuildSubscriptionsView(subscriptionNames),
        };
        return view;
    }

    // -------------------- SQL --------------------

    private static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.boletin_sync','U') IS NULL
            CREATE TABLE dbo.boletin_sync(
              id INT IDENTITY(1,1) PRIMARY KEY,
              client_id INT NOT NULL,
              status NVARCHAR(20) NOT NULL DEFAULT 'running',
              started_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
              finished_at DATETIME2 NULL,
              subscriptions_scanned INT NOT NULL DEFAULT 0,
              advisor_items INT NOT NULL DEFAULT 0,
              health_items INT NOT NULL DEFAULT 0,
              error NVARCHAR(MAX) NULL,
              created_by NVARCHAR(256) NULL);
            IF OBJECT_ID('dbo.boletin_retirement','U') IS NULL
            CREATE TABLE dbo.boletin_retirement(
              id INT IDENTITY(1,1) PRIMARY KEY,
              client_id INT NOT NULL,
              fingerprint BINARY(32) NOT NULL,
              source NVARCHAR(20) NOT NULL,
              announcement_key NVARCHAR(256) NOT NULL,
              subscription_id NVARCHAR(64) NOT NULL,
              azure_resource_id NVARCHAR(1024) NULL,
              resource_name NVARCHAR(256) NOT NULL DEFAULT(''),
              resource_type NVARCHAR(256) NOT NULL DEFAULT(''),
              retiring_feature NVARCHAR(256) NOT NULL DEFAULT(''),
              retirement_date DATE NULL,
              title NVARCHAR(512) NOT NULL DEFAULT(''),
              summary NVARCHAR(MAX) NULL,
              recommended_action NVARCHAR(MAX) NULL,
              learn_more_url NVARCHAR(1024) NULL,
              status NVARCHAR(16) NOT NULL DEFAULT 'vigente',
              first_seen_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
              last_seen_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
              resolved_at DATETIME2 NULL,
              CONSTRAINT UX_boletin_retirement UNIQUE(client_id, fingerprint));
            IF EXISTS (SELECT 1 FROM sys.columns
                       WHERE object_id = OBJECT_ID('dbo.boletin_retirement')
                         AND name = 'azure_resource_id' AND max_length = 1024)
                ALTER TABLE dbo.boletin_retirement ALTER COLUMN azure_resource_id NVARCHAR(1024) NULL;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Predicado canónico de suscripciones administradas (mismo de Optimization/WAF/Inventory).</summary>
    private static async Task<IReadOnlyDictionary<int, List<string>>> ManagedSubscriptionsAsync(
        SqlConnection conn, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.credential_id, s.subscription_id
            FROM dbo.client_azure_subscriptions s
            INNER JOIN dbo.client_azure_credentials c ON s.credential_id = c.credential_id
            WHERE s.client_id = @cid AND s.is_active = 1
              AND COALESCE(s.is_managed, 1) = 1 AND c.is_active = 1
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var groups = new Dictionary<int, List<string>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var credId = r.GetInt32(0);
            if (!groups.TryGetValue(credId, out var list)) groups[credId] = list = [];
            list.Add(r.GetString(1));
        }
        return groups;
    }

    /// <summary>Id + nombre visible de las suscripciones administradas del cliente (A2), para que
    /// GetAsync exponga <c>subscriptions</c> y el front muestre el nombre en vez del GUID. Mismo
    /// predicado canónico que <see cref="ManagedSubscriptionsAsync"/> (lectura dedicada porque esa
    /// devuelve group-by-credencial para el sync, no id+nombre para la vista).
    /// subscription_name lo siembra el sync ARM (ver SqlClientSubscriptionStore).</summary>
    private static async Task<IReadOnlyList<(string SubscriptionId, string? Name)>> ManagedSubscriptionNamesAsync(
        SqlConnection conn, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.subscription_id, s.subscription_name
            FROM dbo.client_azure_subscriptions s
            INNER JOIN dbo.client_azure_credentials c ON s.credential_id = c.credential_id
            WHERE s.client_id = @cid AND s.is_active = 1
              AND COALESCE(s.is_managed, 1) = 1 AND c.is_active = 1
            ORDER BY s.subscription_name, s.subscription_id
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var list = new List<(string, string?)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        return list;
    }

    private static async Task<int> CreateSyncAsync(SqlConnection conn, int clientId, string? actor, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.boletin_sync(client_id, created_by) OUTPUT INSERTED.id VALUES (@cid, @by)
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@by", Db(actor)));
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task UpsertAsync(SqlConnection conn, SqlTransaction tx, int clientId, RetirementRow row, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE dbo.boletin_retirement SET
              last_seen_at = SYSUTCDATETIME(), status = 'vigente', resolved_at = NULL,
              retirement_date = @rdate, title = @title, summary = @summary,
              recommended_action = @action, learn_more_url = @url,
              resource_name = @rname, resource_type = @rtype
            WHERE client_id = @cid AND fingerprint = @fp;
            IF @@ROWCOUNT = 0
            INSERT INTO dbo.boletin_retirement(
              client_id, fingerprint, source, announcement_key, subscription_id, azure_resource_id,
              resource_name, resource_type, retiring_feature, retirement_date, title, summary,
              recommended_action, learn_more_url)
            VALUES (@cid, @fp, @source, @akey, @sub, @resid, @rname, @rtype, @feature, @rdate,
                    @title, @summary, @action, @url);
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@fp", row.Fingerprint(clientId)));
        cmd.Parameters.Add(new SqlParameter("@source", row.Source));
        cmd.Parameters.Add(new SqlParameter("@akey", row.AnnouncementKey));
        cmd.Parameters.Add(new SqlParameter("@sub", row.SubscriptionId));
        cmd.Parameters.Add(new SqlParameter("@resid", Db(row.AzureResourceId)));
        cmd.Parameters.Add(new SqlParameter("@rname", row.ResourceName));
        cmd.Parameters.Add(new SqlParameter("@rtype", row.ResourceType));
        cmd.Parameters.Add(new SqlParameter("@feature", row.RetiringFeature));
        cmd.Parameters.Add(new SqlParameter("@rdate", Db(row.RetirementDate?.ToDateTime(TimeOnly.MinValue))));
        cmd.Parameters.Add(new SqlParameter("@title", row.Title));
        cmd.Parameters.Add(new SqlParameter("@summary", Db(row.Summary)));
        cmd.Parameters.Add(new SqlParameter("@action", Db(row.RecommendedAction)));
        cmd.Parameters.Add(new SqlParameter("@url", Db(row.LearnMoreUrl)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Lo no visto en este sync pasa a 'resuelto' (no se borra: histórico). Solo reconcilia
    /// filas de la FUENTE y las suscripciones cuya query corrió sin error en este sync (ver
    /// <see cref="BoletinSyncPlan"/>): así una falla transitoria (credencial o query caída) nunca
    /// "auto-resuelve" avisos vigentes que simplemente no se pudieron consultar. Lista vacía → no-op.</summary>
    private static async Task ReconcileAsync(SqlConnection conn, SqlTransaction tx, int clientId, DateTime syncStart,
        string source, IReadOnlyList<string> successfulSubscriptionIds, CancellationToken ct)
    {
        if (successfulSubscriptionIds.Count == 0) return; // nada exitoso en esta fuente: no toca nada

        var inParams = successfulSubscriptionIds.Select((_, i) => $"@s{i}").ToList();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE dbo.boletin_retirement
            SET status = 'resuelto', resolved_at = SYSUTCDATETIME()
            WHERE client_id = @cid AND status = 'vigente' AND last_seen_at < @start
              AND source = @source AND subscription_id IN ({string.Join(",", inParams)})
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@start", syncStart));
        cmd.Parameters.Add(new SqlParameter("@source", source));
        for (var i = 0; i < successfulSubscriptionIds.Count; i++)
            cmd.Parameters.Add(new SqlParameter($"@s{i}", successfulSubscriptionIds[i]));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task FinalizeSyncAsync(SqlConnection conn, SqlTransaction tx, int syncId,
        int subs, int advisorItems, int healthItems, string status, string? error, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE dbo.boletin_sync SET status = @status, finished_at = SYSUTCDATETIME(),
              subscriptions_scanned = @subs, advisor_items = @adv, health_items = @hea, error = @err
            WHERE id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", syncId));
        cmd.Parameters.Add(new SqlParameter("@subs", subs));
        cmd.Parameters.Add(new SqlParameter("@adv", advisorItems));
        cmd.Parameters.Add(new SqlParameter("@hea", healthItems));
        cmd.Parameters.Add(new SqlParameter("@status", status));
        cmd.Parameters.Add(new SqlParameter("@err", Db(error)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task MarkSyncFailedAsync(SqlConnection conn, int syncId, string error, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.boletin_sync SET status = 'failed', finished_at = SYSUTCDATETIME(), error = @err WHERE id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", syncId));
        cmd.Parameters.Add(new SqlParameter("@err", error.Length > 3900 ? error[..3900] : error));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<StoredRetirement>> LoadVigentesAsync(SqlConnection conn, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint, source, announcement_key, subscription_id, azure_resource_id,
                   resource_name, resource_type, retiring_feature, retirement_date, title,
                   summary, recommended_action, learn_more_url
            FROM dbo.boletin_retirement
            WHERE client_id = @cid AND status = 'vigente'
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var list = new List<StoredRetirement>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new StoredRetirement(
                Convert.ToHexString((byte[])r.GetValue(0)), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
                r.IsDBNull(8) ? null : DateOnly.FromDateTime(r.GetDateTime(8)), r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11),
                r.IsDBNull(12) ? null : r.GetString(12)));
        return list;
    }

    private static async Task<IReadOnlyDictionary<string, object?>?> LoadLastSyncAsync(SqlConnection conn, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TOP 1 id, status, started_at, finished_at, subscriptions_scanned,
                   advisor_items, health_items, error
            FROM dbo.boletin_sync WHERE client_id = @cid ORDER BY started_at DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Dictionary<string, object?>
        {
            ["id"] = r.GetInt32(0), ["status"] = r.GetString(1),
            ["started_at"] = r.GetDateTime(2).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["finished_at"] = r.IsDBNull(3) ? null : r.GetDateTime(3).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["subscriptions_scanned"] = r.GetInt32(4),
            ["advisor_items"] = r.GetInt32(5), ["health_items"] = r.GetInt32(6),
            ["error"] = r.IsDBNull(7) ? null : r.GetString(7),
        };
    }
}

/// <summary>
/// Lógica pura (sin SQL) de la reconciliación por fuente/suscripción exitosa del sync del Boletín
/// (Finding 1) y de la decisión de status/error persistido (Finding 2). Separada de
/// <see cref="BoletinService"/> para poder testearla sin credenciales/BD reales — mismo patrón que
/// <c>successfulSubscriptions</c> del WAF sync (<see cref="Waf.WafSyncOrchestrator"/>), pero aquí hay
/// DOS fuentes independientes (advisor / service_health) por credencial.
/// </summary>
internal static class BoletinSyncPlan
{
    /// <summary>
    /// Calcula, por fuente, las subscription_id cuya query corrió sin error en este sync. Reglas:
    /// - Credencial no disponible (no se pudo obtener token): excluye AMBAS fuentes de sus subs.
    /// - Query de una sola fuente fallida: excluye solo esa fuente (la otra puede seguir exitosa).
    /// - Sin fallas: todas las subs del grupo son exitosas en ambas fuentes.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> SuccessfulSubscriptionsBySource(
        IReadOnlyDictionary<int, List<string>> groups,
        IReadOnlySet<int> failedCredentials,
        IReadOnlySet<int> advisorFailedCredentials,
        IReadOnlySet<int> healthFailedCredentials)
    {
        var advisor = new List<string>();
        var health = new List<string>();

        foreach (var (credentialId, subIds) in groups)
        {
            if (failedCredentials.Contains(credentialId)) continue; // credencial caída: ninguna fuente
            if (!advisorFailedCredentials.Contains(credentialId)) advisor.AddRange(subIds);
            if (!healthFailedCredentials.Contains(credentialId)) health.AddRange(subIds);
        }

        return new Dictionary<string, IReadOnlyList<string>>
        {
            [RetirementRow.SourceAdvisor] = advisor,
            [RetirementRow.SourceServiceHealth] = health,
        };
    }

    /// <summary>Decide el status y el error persistido de <c>boletin_sync</c> a partir de los
    /// errores acumulados en la corrida: sin errores → 'completed'/NULL; con errores parciales
    /// (aunque hayan fallado todas las credenciales) → 'partial' + JSON de la lista de errores.</summary>
    public static (string Status, string? ErrorJson) DetermineOutcome(IReadOnlyCollection<object> errors) =>
        errors.Count == 0 ? ("completed", null) : ("partial", JsonSerializer.Serialize(errors));
}
