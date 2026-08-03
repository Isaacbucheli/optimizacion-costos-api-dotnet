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
    IBoletinTranslationService translation, ILogger<BoletinService> logger) : IBoletinService
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
            // Enriquecimiento de Service Health (ver más abajo): credenciales cuya query de recursos
            // impactados falló. No excluye la fuente "health" en sí, pero SÍ limita el alcance de su
            // reconciliación (Finding: enriquecimiento caído no debe auto-resolver resource-level).
            var healthResourcesFailedCredentials = new HashSet<int>();

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
                // fallida (no entra a healthFailedCredentials) — la query base de health pudo ir
                // bien y esos avisos se siguen viendo a nivel de suscripción, igual que antes de A1.
                // PERO sí limita el ALCANCE de la reconciliación: sin poder re-consultar qué recursos
                // siguen impactados, no sabemos si las filas resource-level de un sync anterior
                // siguen vigentes, así que ReconcileAsync no debe auto-resolverlas — solo las
                // sub-level (azure_resource_id IS NULL). Por eso esta credencial se registra en
                // healthResourcesFailedCredentials, que BoletinSyncPlan.HealthReconcileScopes usa
                // para separar el alcance "completo" del alcance "solo sub-level".
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
                        "boletin sync {Sync}: recursos impactados de service health falló credencial {Cred} (enriquecimiento; limita la reconciliación de health a solo sub-level)",
                        syncId, credentialId);
                    errors.Add(new { source = "service_health_resources", credential_id = credentialId, error = ex.GetType().Name });
                    healthResourcesFailedCredentials.Add(credentialId);
                }

                subsScanned += subIds.Count;
            }

            rows.AddRange(BoletinParsers.ExpandHealthRows(healthRows, healthImpacted));

            var successfulBySource = BoletinSyncPlan.SuccessfulSubscriptionsBySource(
                groups, failedCredentials, advisorFailedCredentials, healthFailedCredentials);
            var healthScopes = BoletinSyncPlan.HealthReconcileScopes(
                groups, failedCredentials, healthFailedCredentials, healthResourcesFailedCredentials);

            await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct))
            {
                foreach (var row in rows) await UpsertAsync(conn, tx, clientId, row, ct);
                await ReconcileAsync(conn, tx, clientId, syncStart,
                    RetirementRow.SourceAdvisor, successfulBySource[RetirementRow.SourceAdvisor], subLevelOnly: false, ct);
                // service_health se reconcilia en dos pasadas: alcance completo para las subs con
                // enriquecimiento OK (igual que antes), y solo sub-level para las subs con base de
                // health OK pero enriquecimiento caído (no auto-resuelve resource-level ya persistidas).
                await ReconcileAsync(conn, tx, clientId, syncStart,
                    RetirementRow.SourceServiceHealth, healthScopes.FullScope, subLevelOnly: false, ct);
                await ReconcileAsync(conn, tx, clientId, syncStart,
                    RetirementRow.SourceServiceHealth, healthScopes.SubLevelOnly, subLevelOnly: true, ct);
                // Solo counts: el status/error final se decide después de la traducción (ver abajo),
                // así que finished_at/status/error NO se tocan acá — la fila queda 'running' hasta entonces.
                await FinalizeSyncCountsAsync(conn, tx, syncId, subsScanned, advisorCount, healthCount, ct);
                await tx.CommitAsync(ct);
            }

            // Traducción fiel es (best-effort, FUERA de la transacción: la IA es lenta y ajena a los datos).
            // IA no configurada = se omite en silencio (el front cae al EN). Configurada y fallando = error visible.
            if (translation.IsConfigured)
            {
                try { await TranslatePendingAsync(conn, clientId, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "boletin sync {Sync}: traduccion fallo", syncId);
                    errors.Add(new { source = "traduccion", error = ex.GetType().Name });
                }
            }

            // INVARIANTE: el status persistido SIEMPRE refleja los errores de traducción (por eso
            // DetermineOutcome corre acá, después del bloque de traducción, y no antes de la tx).
            var (status, errorJson) = BoletinSyncPlan.DetermineOutcome(errors);
            await UpdateSyncOutcomeAsync(conn, syncId, status, errorJson, ct);

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
            IF COL_LENGTH('dbo.boletin_retirement', 'title_es') IS NULL
                ALTER TABLE dbo.boletin_retirement ADD
                  title_es NVARCHAR(512) NULL,
                  summary_es NVARCHAR(MAX) NULL,
                  recommended_action_es NVARCHAR(MAX) NULL;
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
              retirement_date = @rdate,
              -- El original EN manda: si Azure cambió el texto, la traducción se invalida y se re-traduce.
              title_es = CASE WHEN title <> @title THEN NULL ELSE title_es END,
              summary_es = CASE WHEN ISNULL(summary, N'') <> ISNULL(@summary, N'') THEN NULL ELSE summary_es END,
              recommended_action_es = CASE WHEN ISNULL(recommended_action, N'') <> ISNULL(@action, N'') THEN NULL ELSE recommended_action_es END,
              title = @title, summary = @summary,
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
    /// "auto-resuelve" avisos vigentes que simplemente no se pudieron consultar. Lista vacía → no-op.
    /// <paramref name="subLevelOnly"/> restringe además a filas sub-level (azure_resource_id IS
    /// NULL): lo usa service_health cuando el enriquecimiento de recursos impactados falló para la
    /// credencial de esa suscripción (ver <see cref="BoletinSyncPlan.HealthReconcileScopes"/>) — no
    /// sabemos si las filas resource-level de un sync anterior siguen vigentes, así que no se
    /// tocan. El flag alterna entre dos literales SQL fijos, nunca interpola valores.</summary>
    private static async Task ReconcileAsync(SqlConnection conn, SqlTransaction tx, int clientId, DateTime syncStart,
        string source, IReadOnlyList<string> successfulSubscriptionIds, bool subLevelOnly, CancellationToken ct)
    {
        if (successfulSubscriptionIds.Count == 0) return; // nada exitoso en este alcance: no toca nada

        var inParams = successfulSubscriptionIds.Select((_, i) => $"@s{i}").ToList();
        var scopeClause = subLevelOnly ? "AND azure_resource_id IS NULL" : "";
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE dbo.boletin_retirement
            SET status = 'resuelto', resolved_at = SYSUTCDATETIME()
            WHERE client_id = @cid AND status = 'vigente' AND last_seen_at < @start
              AND source = @source AND subscription_id IN ({string.Join(",", inParams)})
              {scopeClause}
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@start", syncStart));
        cmd.Parameters.Add(new SqlParameter("@source", source));
        for (var i = 0; i < successfulSubscriptionIds.Count; i++)
            cmd.Parameters.Add(new SqlParameter($"@s{i}", successfulSubscriptionIds[i]));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Escribe solo los counts dentro de la transacción del sync (status/error/finished_at
    /// se deciden después, ver <see cref="UpdateSyncOutcomeAsync"/>): la traducción corre fuera de la
    /// tx y puede aportar sus propios errores al status final.</summary>
    private static async Task FinalizeSyncCountsAsync(SqlConnection conn, SqlTransaction tx, int syncId,
        int subs, int advisorItems, int healthItems, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE dbo.boletin_sync SET
              subscriptions_scanned = @subs, advisor_items = @adv, health_items = @hea
            WHERE id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", syncId));
        cmd.Parameters.Add(new SqlParameter("@subs", subs));
        cmd.Parameters.Add(new SqlParameter("@adv", advisorItems));
        cmd.Parameters.Add(new SqlParameter("@hea", healthItems));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Cierra el sync (status/error/finished_at) DESPUÉS del intento de traducción, fuera de
    /// la transacción principal: statement único, no necesita tx propia.</summary>
    private static async Task UpdateSyncOutcomeAsync(SqlConnection conn, int syncId,
        string status, string? error, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.boletin_sync SET status = @status, finished_at = SYSUTCDATETIME(), error = @err
            WHERE id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", syncId));
        cmd.Parameters.Add(new SqlParameter("@status", status));
        cmd.Parameters.Add(new SqlParameter("@err", Db(error)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Traduce en lote los textos vigentes sin traducción (title/summary/recommended_action).
    /// Trabaja por TEXTO DISTINTO (los ~35 anuncios comparten texto entre sus filas de recursos).</summary>
    private async Task TranslatePendingAsync(SqlConnection conn, int clientId, CancellationToken ct)
    {
        // Los nombres de columna vienen de esta tupla constante del propio método, nunca de input de
        // usuario: no hay riesgo de inyección al interpolarlos en el SQL de abajo.
        foreach (var (column, columnEs, maxLen) in new[]
                 {
                     ("title", "title_es", 512),
                     ("summary", "summary_es", 0),
                     ("recommended_action", "recommended_action_es", 0),
                 })
        {
            var pending = new List<string>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT DISTINCT {column} FROM dbo.boletin_retirement
                    WHERE client_id = @cid AND status = 'vigente'
                      AND {columnEs} IS NULL AND ISNULL({column}, N'') <> N''
                    """;
                cmd.Parameters.Add(new SqlParameter("@cid", clientId));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) pending.Add(r.GetString(0));
            }
            if (pending.Count == 0) continue;

            var translated = await translation.TranslateToSpanishAsync(
                pending.Select((t, i) => new BoletinTranslationItem(i.ToString(), t)).ToList(), ct);

            for (var i = 0; i < pending.Count; i++)
            {
                var es = translated[i].Text;
                if (maxLen > 0 && es.Length > maxLen) es = es[..maxLen];
                await using var upd = conn.CreateCommand();
                upd.CommandText = $"""
                    UPDATE dbo.boletin_retirement SET {columnEs} = @es
                    WHERE client_id = @cid AND {column} = @en AND {columnEs} IS NULL
                    """;
                upd.Parameters.Add(new SqlParameter("@cid", clientId));
                upd.Parameters.Add(new SqlParameter("@en", pending[i]));
                upd.Parameters.Add(new SqlParameter("@es", es));
                await upd.ExecuteNonQueryAsync(ct);
            }
        }
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
                   summary, recommended_action, learn_more_url,
                   title_es, summary_es, recommended_action_es
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
                r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) ? null : r.GetString(13),
                r.IsDBNull(14) ? null : r.GetString(14),
                r.IsDBNull(15) ? null : r.GetString(15)));
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

    /// <summary>
    /// Divide las suscripciones de <c>service_health</c> en dos alcances de reconciliación, según si
    /// el ENRIQUECIMIENTO (ServiceHealthImpactedResources) corrió sin error para la credencial de esa
    /// sub. Corrige un caso real: sync N con enriquecimiento OK crea filas resource-level para un
    /// aviso; sync N+1 la query base de health sigue OK pero el enriquecimiento falla para esa
    /// credencial → <c>ExpandHealthRows</c> solo re-emite la fila sub-level (no sabe qué recursos
    /// siguen impactados). Si se reconciliara "todo" igual que antes, las filas resource-level del
    /// sync N (que no se volvieron a ver) se marcarían 'resuelto' por una falla transitoria de una
    /// query best-effort, corrompiendo el histórico <c>resolved_at</c>.
    /// - FullScope: base de health OK + enriquecimiento OK → se reconcilian TODAS las filas de esas
    ///   subs (sub-level y resource-level), como siempre.
    /// - SubLevelOnly: base de health OK pero enriquecimiento FALLÓ → se reconcilian SOLO las filas
    ///   sub-level (azure_resource_id IS NULL); las resource-level no se tocan porque no se sabe si
    ///   siguen vigentes.
    /// - Credencial caída o query base de health fallida: la sub queda fuera de AMBOS alcances
    ///   (misma precedencia que <see cref="SuccessfulSubscriptionsBySource"/>).
    /// No aplica a <c>advisor</c>: esa fuente no tiene enriquecimiento.
    /// </summary>
    public static (IReadOnlyList<string> FullScope, IReadOnlyList<string> SubLevelOnly) HealthReconcileScopes(
        IReadOnlyDictionary<int, List<string>> groups,
        IReadOnlySet<int> failedCredentials,
        IReadOnlySet<int> healthFailedCredentials,
        IReadOnlySet<int> healthResourcesFailedCredentials)
    {
        var full = new List<string>();
        var subLevelOnly = new List<string>();

        foreach (var (credentialId, subIds) in groups)
        {
            if (failedCredentials.Contains(credentialId)) continue; // credencial caída: fuera de ambos
            if (healthFailedCredentials.Contains(credentialId)) continue; // base de health caída: fuera de ambos

            if (healthResourcesFailedCredentials.Contains(credentialId)) subLevelOnly.AddRange(subIds);
            else full.AddRange(subIds);
        }

        return (full, subLevelOnly);
    }
}
