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

/// <summary>Alcance de <see cref="BoletinService.ReconcileAsync"/> (Task 5, reemplaza el antiguo
/// <c>bool subLevelOnly</c> de 2 niveles). <c>Full</c>: reconcilia todo (sub-level, resource-level
/// confirmadas y derivadas). <c>ExcludeDerived</c>: reconcilia todo MENOS las filas derivadas
/// (<c>derived = 1</c>) — se usa cuando el detector de inventario no pudo re-consultarse esta vez y
/// no se sabe si esas filas siguen vigentes. <c>SubLevelOnly</c>: solo filas sin recurso
/// (<c>azure_resource_id IS NULL</c>) — el más restrictivo, para cuando el enriquecimiento de
/// Microsoft cayó y ninguna fila resource-level (confirmada o derivada) puede reconfirmarse.</summary>
internal enum BoletinReconcileMode { Full, ExcludeDerived, SubLevelOnly }

/// <summary>Sync y lectura del Boletín Azure. Patrón del módulo Optimization:
/// ARG por credencial del cliente + persistencia con reconciliación por fingerprint.</summary>
public sealed class BoletinService(
    ISqlConnectionFactory factory, IResourceGraphRunner rg, IAzureCredentialFactory credentials,
    IBoletinTranslationService translation, ISiteRuntimeArmClient siteRuntimes,
    IBoletinLifecycleStore lifecycle, ILogger<BoletinService> logger) : IBoletinService
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
            var advisorCount = 0; var healthCount = 0; var eolCount = 0; var subsScanned = 0;
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
            // Detectores de inventario (Task 5): credenciales cuya re-consulta de runtimes
            // (LinuxSiteRuntimes/WindowsSites) falló, O cuya cobertura quedó incompleta sin lanzar
            // excepción (Task 6: WindowsSites es best-effort por sitio vía ARM — FailedCount>0 o
            // Truncated=true no son un catch, son un resultado parcial "exitoso"). Igual que
            // healthResourcesFailedCredentials, NO excluye la fuente "health", pero limita el alcance
            // de la reconciliación: sin el inventario completo, no se auto-resuelven las filas
            // DERIVADAS de esa sub (ver BoletinSyncPlan.HealthReconcileScopes).
            var detectorFailedCredentials = new HashSet<int>();

            // Fin de soporte (Task 4, fuente "eol"): catálogo de lifecycle leído UNA sola vez antes
            // del loop por credencial (best-effort, es global — no cambia por credencial/suscripción).
            // DESVIACIÓN del brief: eolFailedCredentials se puebla SOLO en el catch (catálogo
            // ilegible), no cuando eolEnabled queda en false por un catálogo legible pero sin
            // entradas activas. Un catálogo vacío es un estado CONOCIDO (no hay nada que matchear),
            // así que el sync igual reconcilia "eol" con normalidad (Full, todas las subs exitosas) y
            // resuelve las filas vigentes que ya no tienen contra qué reconfirmarse — muy distinto de
            // "no sabemos si el catálogo sigue vigente" (ilegible), que sí debe abstenerse de tocar
            // nada (ver self-review en task-4-report.md).
            IReadOnlyList<LifecycleEntry> lifecycleEntries = [];
            var eolEnabled = false;
            var eolFailedCredentials = new HashSet<int>();
            try
            {
                lifecycleEntries = await lifecycle.ListAsync(includeInactive: false, ct);
                eolEnabled = lifecycleEntries.Count > 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "boletin sync {Sync}: catalogo lifecycle ilegible", syncId);
                errors.Add(new { source = "fin_de_soporte", error = ex.GetType().Name });
                // Catálogo desconocido ⇒ TODAS las credenciales fuera de "eol" este sync (no
                // reconciliar eol con catálogo desconocido).
                eolFailedCredentials = groups.Keys.ToHashSet();
            }

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

                // healthAnnouncements queda accesible DESPUÉS del try (no como el `parsed` local de
                // los otros bloques): el detector de inventario, más abajo en este mismo loop, necesita
                // los avisos de ESTA credencial para saber contra qué títulos matchear el runtime.
                var healthAnnouncements = new List<RetirementRow>();
                try
                {
                    var nodes = await rg.RunQueryAsync(cred, subIds, BoletinQueries.ServiceHealthRetirements, ct);
                    healthAnnouncements = nodes.Select(n => BoletinParsers.FromHealthRow(new RgRow(n)))
                                      .Where(r => r is not null).Select(r => r!).ToList();
                    healthCount += healthAnnouncements.Count;
                    healthRows.AddRange(healthAnnouncements);
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

                // Detectores de inventario (Task 5): para los avisos de Service Health de ESTA
                // credencial cuyo título matchea un runtime conocido (Node/Python/PHP/PowerShell/
                // Java/.NET — ver BoletinDetectors.MatchAnnouncement), re-consulta el inventario de
                // sitios y deriva filas resource-level para los que corren ese runtime. Solo entra
                // acá si algún título matcheó (evita 2 queries ARG extra por credencial cuando no
                // hace falta). Best-effort: si falla, NO se marca "service_health" como fallida (los
                // avisos ya se vieron bien) — solo se registra en detectorFailedCredentials para que
                // HealthReconcileScopes no auto-resuelva las filas derivadas de esta sub.
                var announcementTargets = healthAnnouncements
                    .Select(h => (Row: h, Targets: BoletinDetectors.MatchAnnouncement(h.Title)))
                    .Where(x => x.Targets.Count > 0)
                    .ToList();
                if (announcementTargets.Count > 0)
                {
                    try
                    {
                        var siteNodes = await rg.RunQueryAsync(cred, subIds, BoletinQueries.LinuxSiteRuntimes, ct);
                        var sites = siteNodes.Select(n => BoletinDetectors.FromLinuxSiteRow(new RgRow(n)))
                                             .Where(s => s is not null).Select(s => s!).ToList();
                        // Task 6: runtimes de Windows vía ARM. A diferencia de las demás fuentes, un
                        // fallo parcial acá (sitios que no respondieron config/web, o lote recortado a
                        // MaxSites) NO lanza excepción — FetchAsync es best-effort por sitio. Por eso se
                        // revisa explícitamente FailedCount/Truncated en vez de confiar solo en el catch.
                        // Solo interesan los sitios de las subs con AL MENOS un aviso matcheado (Item 1
                        // fase 2): consultar config/web de sitios en subs sin ningún target es puro
                        // desperdicio (BuildDerivedRows los descarta después de todos modos), y con hasta
                        // 300 GETs por lote ese desperdicio es justo lo que empuja el sync sobre el
                        // timeout del App Service en producción.
                        var targetSubs = announcementTargets
                            .Select(x => x.Row.SubscriptionId)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var winResult = await FetchWindowsRuntimesAsync(cred, subIds, targetSubs, ct);
                        sites.AddRange(winResult.Sites);
                        foreach (var w in winResult.Warnings)
                            errors.Add(new { source = "inventario", credential_id = credentialId, warning = w });
                        if (winResult.FailedCount > 0 || winResult.Truncated)
                        {
                            // Cobertura Windows incompleta ⇒ las filas derivadas de esta credencial no
                            // se reconcilian este sync (scope ExcludeDerived, no FullScope): sin el
                            // lote completo de sitios no sabemos si siguen corriendo el runtime que
                            // origina la fila derivada. DICTAMEN del controlador: el truncado a
                            // MaxSites TAMBIÉN degrada (no solo los timeouts por sitio), y el sync
                            // queda 'partial' a propósito — misma doctrina de transparencia sobre
                            // cobertura incompleta que el Advisor sync (nextLink roto → estado parcial
                            // visible, nunca un silencio que se confunda con éxito).
                            detectorFailedCredentials.Add(credentialId);
                        }
                        // La dedup es POR ANUNCIO (Item 2 del review): solo los resource IDs que YA
                        // están en `rows` bajo ESTE MISMO anuncio (mismo Source+AnnouncementKey) cuentan
                        // como "existentes". Un `existing` global (todas las filas acumuladas de
                        // CUALQUIER anuncio) suprimía derivadas legítimas cuando el mismo recurso
                        // aparecía bajo un anuncio distinto — un mismo sitio puede estar impactado por
                        // varios retiros a la vez, y cada uno lo debe reportar por separado. El caso que
                        // el `existing` global pretendía cubrir (no duplicar el enriquecimiento del
                        // MISMO aviso) ya lo resuelve el orden de upsert + ratchet por fingerprint en
                        // UpsertAsync — y de todos modos ExpandHealthRows (que puebla las filas
                        // resource-level confirmadas) corre DESPUÉS de este loop, así que ni siquiera
                        // estaban en `rows` todavía en este punto.
                        foreach (var (announcement, targets) in announcementTargets)
                        {
                            var existing = rows
                                .Where(r => r.Source == announcement.Source
                                    && r.AnnouncementKey == announcement.AnnouncementKey
                                    && r.AzureResourceId is not null)
                                .Select(r => r.AzureResourceId!.ToLowerInvariant())
                                .ToHashSet(StringComparer.Ordinal);
                            rows.AddRange(BoletinDetectors.BuildDerivedRows(announcement, targets, sites, existing));
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "boletin sync {Sync}: detector de inventario fallo credencial {Cred}", syncId, credentialId);
                        detectorFailedCredentials.Add(credentialId);
                        errors.Add(new { source = "inventario", credential_id = credentialId, error = ex.GetType().Name });
                    }
                }

                // Fin de soporte (Task 4): inventario propio (SO por VM + imagen SQL) cruzado contra
                // el catálogo de lifecycle. Solo corre si el catálogo se pudo leer y tiene entradas
                // activas (eolEnabled) — sin eso no hay contra qué matchear. Best-effort por
                // credencial, igual que advisor/service_health: si esta credencial falla, NO apaga
                // "eol" para las demás, solo la excluye a ELLA de la reconciliación de esa fuente.
                if (eolEnabled)
                {
                    try
                    {
                        var vmNodes = await rg.RunQueryAsync(cred, subIds, BoletinQueries.VmOsInventory, ct);
                        var sqlNodes = await rg.RunQueryAsync(cred, subIds, BoletinQueries.SqlVmImages, ct);
                        var eolResources = vmNodes.Select(n => BoletinEol.FromVmOsRow(new RgRow(n)))
                            .Concat(sqlNodes.Select(n => BoletinEol.FromSqlVmRow(new RgRow(n))))
                            .Where(r => r is not null).Select(r => r!).ToList();
                        var eolRows = BoletinEol.MatchResources(lifecycleEntries, eolResources);
                        rows.AddRange(eolRows);
                        eolCount += eolRows.Count;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "boletin sync {Sync}: inventario eol fallo credencial {Cred}", syncId, credentialId);
                        eolFailedCredentials.Add(credentialId);
                        errors.Add(new { source = "fin_de_soporte", credential_id = credentialId, error = ex.GetType().Name });
                    }
                }

                subsScanned += subIds.Count;
            }

            rows.AddRange(BoletinParsers.ExpandHealthRows(healthRows, healthImpacted));

            var successfulBySource = BoletinSyncPlan.SuccessfulSubscriptionsBySource(
                groups, failedCredentials, advisorFailedCredentials, healthFailedCredentials, eolFailedCredentials);
            var healthScopes = BoletinSyncPlan.HealthReconcileScopes(
                groups, failedCredentials, healthFailedCredentials, healthResourcesFailedCredentials, detectorFailedCredentials);

            await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct))
            {
                foreach (var row in rows) await UpsertAsync(conn, tx, clientId, row, ct);
                await ReconcileAsync(conn, tx, clientId, syncStart,
                    RetirementRow.SourceAdvisor, successfulBySource[RetirementRow.SourceAdvisor], BoletinReconcileMode.Full, ct);
                // service_health se reconcilia en TRES pasadas (Task 5 amplía de 2 a 3 niveles), cada
                // una con el alcance más amplio que su falla permite (ver HealthReconcileScopes):
                // Full para subs con enriquecimiento Y detector OK (como siempre); ExcludeDerived
                // para subs con enriquecimiento OK pero detector de inventario caído (no auto-resuelve
                // derivadas); SubLevelOnly para subs con enriquecimiento caído (no toca ninguna fila
                // resource-level, confirmada o derivada). Las tres son no-op si su lista viene vacía.
                await ReconcileAsync(conn, tx, clientId, syncStart,
                    RetirementRow.SourceServiceHealth, healthScopes.FullScope, BoletinReconcileMode.Full, ct);
                await ReconcileAsync(conn, tx, clientId, syncStart,
                    RetirementRow.SourceServiceHealth, healthScopes.ExcludeDerived, BoletinReconcileMode.ExcludeDerived, ct);
                await ReconcileAsync(conn, tx, clientId, syncStart,
                    RetirementRow.SourceServiceHealth, healthScopes.SubLevelOnly, BoletinReconcileMode.SubLevelOnly, ct);
                // "eol" (Task 4) reconcilia en un solo paso (Full): a diferencia de service_health no
                // tiene enriquecimiento ni detectores propios, solo credencial caída o catálogo
                // ilegible la excluyen (ambos ya reflejados en successfulBySource). No-op si viene vacía.
                var eolScope = successfulBySource[RetirementRow.SourceEol];
                if (eolScope.Count > 0)
                    await ReconcileAsync(conn, tx, clientId, syncStart, RetirementRow.SourceEol, eolScope, BoletinReconcileMode.Full, ct);
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
                ["advisor_items"] = advisorCount, ["health_items"] = healthCount, ["eol_items"] = eolCount,
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

    /// <summary>Runtimes de apps Windows del lote (ARM). Devuelve el <see cref="SiteRuntimeArmResult"/>
    /// completo (no solo <c>Sites</c>) para que el caller decida, con <c>credentialId</c> a mano, si
    /// la cobertura fue completa: emite los warnings con su <c>credential_id</c> y marca
    /// <c>detectorFailedCredentials</c> cuando <c>FailedCount &gt; 0</c> o <c>Truncated</c> (ver
    /// RunSyncAsync) — un fallo parcial acá NO lanza excepción, así que sin esto la degradación de
    /// cobertura pasaba desapercibida y HealthReconcileScopes reconciliaba (resolvía) filas derivadas
    /// que en realidad no se pudieron re-confirmar.
    /// <paramref name="targetSubscriptionIds"/> (Item 1 fase 2): la query ARG de <c>WindowsSites</c>
    /// sigue corriendo sobre TODAS las <paramref name="subIds"/> del grupo (una sola llamada, barata),
    /// pero los <c>refs</c> que de ahí salen se filtran a solo esas subs ANTES de <c>FetchAsync</c> —
    /// ese es el costoso (hasta 300 GETs config/web por lote); sitios de subs sin ningún aviso
    /// matcheado nunca producirían una fila derivada (BuildDerivedRows exige misma suscripción que el
    /// anuncio), así que consultarlos es desperdicio puro.</summary>
    private async Task<SiteRuntimeArmResult> FetchWindowsRuntimesAsync(
        Azure.Core.TokenCredential cred, IReadOnlyList<string> subIds,
        IReadOnlySet<string> targetSubscriptionIds, CancellationToken ct)
    {
        var nodes = await rg.RunQueryAsync(cred, subIds, BoletinQueries.WindowsSites, ct);
        var refs = new List<WindowsSiteRef>();
        foreach (var n in nodes)
        {
            var row = new OptimizacionCostos.Api.Features.Inventory.RgRow(n);
            var sub = (row.Str("subscriptionId") ?? "").Trim();
            var id = (row.Str("siteId") ?? "").Trim();
            if (sub.Length == 0 || id.Length == 0) continue;
            if (!targetSubscriptionIds.Contains(sub)) continue; // sin avisos matcheados en esta sub
            refs.Add(new WindowsSiteRef(sub, id, (row.Str("name") ?? "").Trim()));
        }
        return await siteRuntimes.FetchAsync(cred, refs, ct);
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
            -- Task 5: distingue filas confirmadas por Microsoft (derived = 0, default) de las
            -- inferidas por los detectores de inventario del BIT (derived = 1). Ver UpsertAsync:
            -- lo confirmado por Microsoft en un sync posterior siempre gana sobre lo inferido.
            IF COL_LENGTH('dbo.boletin_retirement', 'derived') IS NULL
                ALTER TABLE dbo.boletin_retirement ADD derived BIT NOT NULL DEFAULT(0);
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
              resource_name = @rname, resource_type = @rtype,
              -- Task 5: SIEMPRE se SETea (no solo en el INSERT). Dentro de UN MISMO sync, si el
              -- enriquecimiento y un detector reportan el mismo fingerprint, el orden de `rows`
              -- hace que la fila confirmada (derived=0) se upsertee después y pise a la inferida
              -- (0 pisa 1). ENTRE syncs eso no basta: el sync N puede confirmar la fila (derived=0
              -- persistido) y el sync N+1 puede NO traer el enriquecimiento (falla transitoria o
              -- Microsoft deja de publicarla) mientras el detector la re-deriva, ejecutando este
              -- UPDATE con @derived=1 sobre el MISMO fingerprint. El ratchet de abajo cierra ese
              -- hueco: una vez confirmada por Microsoft, la fila jamás vuelve a "inferido" aunque
              -- el @derived entrante sea 1; lo inferido SÍ puede subir a confirmado (0 pisa a 1,
              -- nunca al revés).
              derived = CASE WHEN derived = 0 THEN 0 ELSE @derived END
            WHERE client_id = @cid AND fingerprint = @fp;
            IF @@ROWCOUNT = 0
            INSERT INTO dbo.boletin_retirement(
              client_id, fingerprint, source, announcement_key, subscription_id, azure_resource_id,
              resource_name, resource_type, retiring_feature, retirement_date, title, summary,
              recommended_action, learn_more_url, derived)
            VALUES (@cid, @fp, @source, @akey, @sub, @resid, @rname, @rtype, @feature, @rdate,
                    @title, @summary, @action, @url, @derived);
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
        cmd.Parameters.Add(new SqlParameter("@derived", row.Derived));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Lo no visto en este sync pasa a 'resuelto' (no se borra: histórico). Solo reconcilia
    /// filas de la FUENTE y las suscripciones cuya query corrió sin error en este sync (ver
    /// <see cref="BoletinSyncPlan"/>): así una falla transitoria (credencial o query caída) nunca
    /// "auto-resuelve" avisos vigentes que simplemente no se pudieron consultar. Lista vacía → no-op.
    /// <paramref name="mode"/> (Task 5, reemplaza el antiguo <c>bool subLevelOnly</c>) restringe el
    /// alcance según qué pudo re-consultarse en este sync (ver
    /// <see cref="BoletinSyncPlan.HealthReconcileScopes"/> para la precedencia completa):
    /// - Full: todas las filas de esas subs (sub-level, resource-level confirmadas y derivadas).
    /// - ExcludeDerived: todas MENOS las derivadas (derived = 1) — el detector de inventario no
    ///   pudo re-consultarse, así que no se auto-resuelven filas inferidas por él.
    /// - SubLevelOnly: solo azure_resource_id IS NULL — el enriquecimiento de Microsoft cayó, no se
    ///   toca ninguna fila resource-level (ni confirmada ni derivada).
    /// El SQL de cada modo es un literal fijo (nunca interpola valores del caller).</summary>
    private static async Task ReconcileAsync(SqlConnection conn, SqlTransaction tx, int clientId, DateTime syncStart,
        string source, IReadOnlyList<string> successfulSubscriptionIds, BoletinReconcileMode mode, CancellationToken ct)
    {
        if (successfulSubscriptionIds.Count == 0) return; // nada exitoso en este alcance: no toca nada

        var inParams = successfulSubscriptionIds.Select((_, i) => $"@s{i}").ToList();
        var scopeClause = mode switch
        {
            BoletinReconcileMode.ExcludeDerived => "AND (azure_resource_id IS NULL OR derived = 0)",
            BoletinReconcileMode.SubLevelOnly => "AND azure_resource_id IS NULL",
            _ => "",
        };
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
                      -- El contenido eol es ES-nativo (catálogo BIT): no se traduce.
                      AND source <> 'eol'
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
                   title_es, summary_es, recommended_action_es, derived
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
                r.IsDBNull(15) ? null : r.GetString(15),
                r.GetBoolean(16)));
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
    /// - Credencial no disponible (no se pudo obtener token): excluye TODAS las fuentes de sus subs.
    /// - Query de una sola fuente fallida: excluye solo esa fuente (las demás pueden seguir exitosas).
    /// - Sin fallas: todas las subs del grupo son exitosas en las tres fuentes.
    /// <c>eol</c> (Task 4, fin de soporte por catálogo) es una tercera fuente independiente de
    /// advisor/service_health: su propio inventario (VmOsInventory/SqlVmImages) puede fallar por
    /// credencial sin afectar a las otras dos, igual que advisorFailedCredentials/healthFailedCredentials.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> SuccessfulSubscriptionsBySource(
        IReadOnlyDictionary<int, List<string>> groups,
        IReadOnlySet<int> failedCredentials,
        IReadOnlySet<int> advisorFailedCredentials,
        IReadOnlySet<int> healthFailedCredentials,
        IReadOnlySet<int> eolFailedCredentials)
    {
        var advisor = new List<string>();
        var health = new List<string>();
        var eol = new List<string>();

        foreach (var (credentialId, subIds) in groups)
        {
            if (failedCredentials.Contains(credentialId)) continue; // credencial caída: ninguna fuente
            if (!advisorFailedCredentials.Contains(credentialId)) advisor.AddRange(subIds);
            if (!healthFailedCredentials.Contains(credentialId)) health.AddRange(subIds);
            if (!eolFailedCredentials.Contains(credentialId)) eol.AddRange(subIds);
        }

        return new Dictionary<string, IReadOnlyList<string>>
        {
            [RetirementRow.SourceAdvisor] = advisor,
            [RetirementRow.SourceServiceHealth] = health,
            [RetirementRow.SourceEol] = eol,
        };
    }

    /// <summary>Decide el status y el error persistido de <c>boletin_sync</c> a partir de los
    /// errores acumulados en la corrida: sin errores → 'completed'/NULL; con errores parciales
    /// (aunque hayan fallado todas las credenciales) → 'partial' + JSON de la lista de errores.</summary>
    public static (string Status, string? ErrorJson) DetermineOutcome(IReadOnlyCollection<object> errors) =>
        errors.Count == 0 ? ("completed", null) : ("partial", JsonSerializer.Serialize(errors));

    /// <summary>
    /// Divide las suscripciones de <c>service_health</c> en TRES alcances de reconciliación (Task 5:
    /// suma el detector de inventario a la precedencia de <c>SuccessfulSubscriptionsBySource</c>).
    /// Precedencia, del más restrictivo al más amplio:
    /// 1. Credencial caída o query base de health fallida → la sub queda fuera de los TRES alcances
    ///    (misma precedencia que <see cref="SuccessfulSubscriptionsBySource"/>; no aparece ni en
    ///    FullScope, ni en ExcludeDerived, ni en SubLevelOnly).
    /// 2. Enriquecimiento (ServiceHealthImpactedResources) caído → SubLevelOnly: ninguna fila
    ///    resource-level se toca (ni confirmada ni derivada), porque no se sabe si sigue vigente.
    ///    Corrige un caso real: sync N con enriquecimiento OK crea filas resource-level para un
    ///    aviso; sync N+1 la base de health sigue OK pero el enriquecimiento falla para esa
    ///    credencial → <c>ExpandHealthRows</c> solo re-emite la fila sub-level. Si se reconciliara
    ///    "todo" igual que antes, esas filas resource-level se marcarían 'resuelto' por una falla
    ///    transitoria de una query best-effort, corrompiendo el histórico <c>resolved_at</c>.
    /// 3. Enriquecimiento OK pero detector de inventario (LinuxSiteRuntimes/WindowsSites) caído →
    ///    ExcludeDerived: las filas CONFIRMADAS por Microsoft sí se reconcilian normalmente (el
    ///    enriquecimiento funcionó), pero las DERIVADAS (inferidas por BIT) no se tocan — sin poder
    ///    re-consultar el inventario, no sabemos si el sitio sigue existiendo/con ese runtime, así
    ///    que no se auto-resuelven derivadas por una falla transitoria del detector.
    /// 4. Todo OK → FullScope: se reconcilian TODAS las filas (sub-level, resource-level confirmadas
    ///    y derivadas) de esas subs, como antes de Task 5.
    /// El enriquecimiento manda sobre el detector (2 antes que 3): si ambos cayeron, importa más no
    /// tocar NINGUNA fila resource-level (ni siquiera las derivadas) que solo proteger las derivadas.
    /// No aplica a <c>advisor</c>: esa fuente no tiene enriquecimiento ni detectores.
    /// </summary>
    public static (IReadOnlyList<string> FullScope, IReadOnlyList<string> ExcludeDerived, IReadOnlyList<string> SubLevelOnly)
        HealthReconcileScopes(
            IReadOnlyDictionary<int, List<string>> groups,
            IReadOnlySet<int> failedCredentials,
            IReadOnlySet<int> healthFailedCredentials,
            IReadOnlySet<int> healthResourcesFailedCredentials,
            IReadOnlySet<int> detectorFailedCredentials)
    {
        var full = new List<string>();
        var excludeDerived = new List<string>();
        var subLevelOnly = new List<string>();

        foreach (var (credentialId, subIds) in groups)
        {
            if (failedCredentials.Contains(credentialId)) continue; // credencial caída: fuera de los tres
            if (healthFailedCredentials.Contains(credentialId)) continue; // base de health caída: fuera de los tres

            if (healthResourcesFailedCredentials.Contains(credentialId)) subLevelOnly.AddRange(subIds); // el más restrictivo
            else if (detectorFailedCredentials.Contains(credentialId)) excludeDerived.AddRange(subIds);
            else full.AddRange(subIds);
        }

        return (full, excludeDerived, subLevelOnly);
    }
}
