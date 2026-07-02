using System.Text.Json;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.AzureIntegration;
using OptimizacionCostos.Api.Features.CostEngine.Engine;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using OptimizacionCostos.Api.Features.FinOpsData;

namespace OptimizacionCostos.Api.Features.Inventory;

/// <summary>Opciones del import (port de ImportRequest).</summary>
public sealed record ImportOptions(
    int? CredentialId = null,
    IReadOnlyList<string>? Subscriptions = null,
    IReadOnlyList<string>? Services = null,
    bool ReplaceExisting = true);

/// <summary>Algún parámetro del import es inválido (→ 400).</summary>
public sealed class ImportBadRequestException(string message) : Exception(message);
/// <summary>El análisis no existe (→ 404).</summary>
public sealed class AnalysisNotFoundException(int analysisId) : Exception($"Analysis not found: {analysisId}");

/// <summary>
/// Orquesta la importación de inventario y el descubrimiento de tipos de recurso. Port de
/// app/routes/azure_import.py. Usa el catálogo (KQL), B1 (Resource Graph + credenciales),
/// B3 (sync de suscripciones) y los inserters de inventario.
/// </summary>
public interface IInventoryImportService
{
    Task<IReadOnlyDictionary<string, object?>> ImportInventoryAsync(int analysisId, ImportOptions opts, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, object?>> DiscoverAsync(int clientId, CancellationToken ct = default);
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SummaryAsync(int analysisId, CancellationToken ct = default);
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListDiscoveredAsync(int clientId, bool onlyUncataloged, bool onlyRelevant, CancellationToken ct = default);
}

public sealed class InventoryImportService(
    ISqlConnectionFactory factory,
    IServiceCatalog catalog,
    IResourceGraphRunner rg,
    IAzureCredentialFactory credentials,
    ISubscriptionSyncService sync,
    IChatCompletionClient chat,
    AppConfig config,
    ILogger<InventoryImportService> logger,
    IFinOpsRefData? refData = null) : IInventoryImportService
{
    private const string DiscoveryKql =
        "Resources | summarize resource_count=count(), sample_names=make_set(name, 5) by resource_type=tolower(type) | order by resource_count desc";

    private sealed record SubGroup(string? CredentialName, Dictionary<string, string?> Subscriptions);

    // -------------------- IMPORT --------------------

    public async Task<IReadOnlyDictionary<string, object?>> ImportInventoryAsync(int analysisId, ImportOptions opts, CancellationToken ct = default)
    {
        var services = ResolveServicesToImport(opts.Services);
        if (services.Count == 0)
            throw new ImportBadRequestException("No services to import");

        await using var conn = await factory.OpenAsync(ct);
        var clientId = await GetAnalysisClientIdAsync(conn, analysisId, ct);

        // Descubrimiento + sync (cada uno con su propia conexión, igual que el Python).
        var discoverySummary = await DiscoverAsync(clientId, ct);
        var syncSummary = await sync.SyncAsync(clientId, ct);

        var (groups, subNameMap) = await ResolveSubscriptionGroupsAsync(conn, clientId, opts.Subscriptions, ct);

        // FASE 1 (sin transacción): correr la KQL de cada servicio por credencial y juntar filas.
        var collected = new List<(ServiceCatalogEntry Service, RgRow Row)>();
        var summary = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var (credentialId, group) in groups)
        {
            var subIds = group.Subscriptions.Keys.ToList();
            Azure.Core.TokenCredential cred;
            try { cred = await credentials.GetClientSecretCredentialAsync(credentialId, ct); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to build credential type={Type}", ex.GetType().Name);
                throw new ImportBadRequestException($"Failed to build Azure credential: {ex.GetType().Name}");
            }

            foreach (var svc in services)
            {
                var s = summary.TryGetValue(svc.ServiceKey, out var existing) ? existing
                    : summary[svc.ServiceKey] = new Dictionary<string, object?> { ["imported"] = 0, ["insert_errors"] = 0, ["errors"] = new List<object>() };
                try
                {
                    var rows = await rg.RunQueryAsync(cred, subIds, svc.KqlQuery ?? "", ct);
                    foreach (var node in rows)
                        collected.Add((svc, new RgRow(node)));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Resource Graph query failed service={Svc} credential={Cred} type={Type}", svc.ServiceKey, credentialId, ex.GetType().Name);
                    var msg = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
                    ((List<object>)s["errors"]!).Add(new { credential_id = credentialId, error = $"{ex.GetType().Name}: {msg}" });
                }
            }
        }

        // FASE 2 (transacción): ensure schema, borrar previo si aplica, insertar todo.
        await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct))
        {
            await InventoryInserter.EnsureSchemaAsync(conn, tx, ct);
            if (opts.ReplaceExisting)
                await DeletePreviousInventoryAsync(conn, tx, analysisId, ct);

            foreach (var (svc, row) in collected)
            {
                var s = summary[svc.ServiceKey];
                try
                {
                    var resourceId = await InventoryInserter.InsertResourceAsync(conn, tx, analysisId, subNameMap, row, svc.ServiceCategory ?? "", ct);
                    await InventoryInserter.InsertDetailAsync(conn, tx, svc.InserterKey ?? "", resourceId, row, ct);
                    s["imported"] = (int)s["imported"]! + 1;
                }
                catch (Exception ex)
                {
                    s["insert_errors"] = (int)s["insert_errors"]! + 1;
                    logger.LogError(ex, "Insert failed service={Svc} type={Type}", svc.ServiceKey, ex.GetType().Name);
                }
            }
            await tx.CommitAsync(ct);
        }

        // Auditoría por credencial (best-effort, fuera de la transacción).
        foreach (var credentialId in groups.Keys)
        {
            try { await credentials.WriteAuditLogAsync(credentialId, "used", details: $"Inventory import for analysis_id={analysisId}", ct: ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Audit write failed credential={Cred}", credentialId); }
        }

        return new Dictionary<string, object?>
        {
            ["message"] = "Importación completada",
            ["analysis_id"] = analysisId,
            ["sync"] = syncSummary,
            ["discovery"] = discoverySummary,
            ["credential_ids"] = groups.Keys.ToList(),
            ["subscriptions"] = subNameMap.Keys.ToList(),
            ["summary"] = summary,
        };
    }

    private List<ServiceCatalogEntry> ResolveServicesToImport(IReadOnlyList<string>? requested)
    {
        var allActive = catalog.ListServices(onlyActive: true);
        if (requested is null || requested.Count == 0)
            return allActive.ToList();

        var withDeps = ServiceDependencies.ExpandServiceKeys(requested) ?? [];
        var activeKeys = allActive.Select(s => s.ServiceKey).ToHashSet();
        var unknown = withDeps.Where(k => !activeKeys.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new ImportBadRequestException($"Unknown or inactive services: {string.Join(", ", unknown)}");

        var requestedSet = withDeps.ToHashSet();
        return allActive.Where(s => requestedSet.Contains(s.ServiceKey)).ToList(); // preserva display_order
    }

    private static async Task<int> GetAnalysisClientIdAsync(SqlConnection conn, int analysisId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_id FROM dbo.cost_analysis WHERE analysis_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", analysisId));
        var v = await cmd.ExecuteScalarAsync(ct);
        if (v is null or DBNull) throw new AnalysisNotFoundException(analysisId);
        return Convert.ToInt32(v);
    }

    private static async Task<(Dictionary<int, SubGroup> Groups, Dictionary<string, string?> SubNameMap)> ResolveSubscriptionGroupsAsync(
        SqlConnection conn, int clientId, IReadOnlyList<string>? requested, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.credential_id, c.credential_name, s.subscription_id, s.subscription_name
            FROM dbo.client_azure_subscriptions s
            INNER JOIN dbo.client_azure_credentials c ON s.credential_id = c.credential_id
            WHERE s.client_id = @cid AND s.is_active = 1 AND COALESCE(s.is_managed, 1) = 1 AND c.is_active = 1
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));

        var rows = new List<(int Cred, string? CredName, string SubId, string? SubName)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rows.Add((r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));

        if (rows.Count == 0)
            throw new ImportBadRequestException($"No active subscriptions for client {clientId}");

        if (requested is { Count: > 0 })
        {
            var available = rows.Select(x => x.SubId).ToHashSet();
            var unknown = requested.Where(s => !available.Contains(s)).ToList();
            if (unknown.Count > 0)
                throw new ImportBadRequestException($"Subscriptions not registered or inactive for this client: {string.Join(", ", unknown)}");
            var keep = requested.ToHashSet();
            rows = rows.Where(x => keep.Contains(x.SubId)).ToList();
        }

        var groups = new Dictionary<int, SubGroup>();
        var subNameMap = new Dictionary<string, string?>();
        foreach (var (cred, credName, subId, subName) in rows)
        {
            if (!groups.TryGetValue(cred, out var g))
                g = groups[cred] = new SubGroup(credName, new Dictionary<string, string?>());
            g.Subscriptions[subId] = subName;
            subNameMap[subId] = subName;
        }
        return (groups, subNameMap);
    }

    private static async Task DeletePreviousInventoryAsync(SqlConnection conn, SqlTransaction tx, int analysisId, CancellationToken ct)
    {
        async Task Exec(string sql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqlParameter("@id", analysisId));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await Exec("DELETE FROM dbo.scenario_breakdown WHERE scenario_id IN (SELECT scenario_id FROM dbo.cost_scenarios WHERE analysis_id = @id)");
        await Exec("DELETE FROM dbo.cost_scenarios WHERE analysis_id = @id");
        await Exec("DELETE FROM dbo.cost_results WHERE analysis_id = @id");
        foreach (var tbl in InventoryInserter.KnownDetailTables)
            await Exec($"IF OBJECT_ID('dbo.{tbl}','U') IS NOT NULL DELETE FROM dbo.{tbl} WHERE resource_id IN (SELECT resource_id FROM dbo.azure_resources WHERE analysis_id = @id)");
        await Exec("DELETE FROM dbo.azure_resources WHERE analysis_id = @id");
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SummaryAsync(int analysisId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT service_category, resource_type, COUNT(*) AS cnt
            FROM dbo.azure_resources WHERE analysis_id = @id
            GROUP BY service_category, resource_type ORDER BY service_category, resource_type
            """;
        cmd.Parameters.Add(new SqlParameter("@id", analysisId));
        var list = new List<IReadOnlyDictionary<string, object?>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Dictionary<string, object?>
            {
                ["service_category"] = r.IsDBNull(0) ? null : r.GetString(0),
                ["resource_type"] = r.IsDBNull(1) ? null : r.GetString(1),
                ["count"] = r.GetInt32(2),
            });

        if (refData is not null)
        {
            try { return EnrichSummary(list, await refData.GetResourceTypesAsync(ct)); }
            catch (Exception ex) { logger.LogWarning(ex, "Enriquecimiento FinOps del summary fallo; se devuelve sin enriquecer"); }
        }
        return list;
    }

    /// <summary>
    /// Enriquece las filas del GROUP BY del summary con display_name y finops_category del
    /// catálogo de tipos de recurso FinOps Toolkit (null si el tipo no está en las tablas ref).
    /// Método puro, sin SQL, para poder probarlo sin BD.
    /// </summary>
    internal static List<Dictionary<string, object?>> EnrichSummary(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyDictionary<string, FinOpsResourceTypeInfo> types)
    {
        var result = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var d = new Dictionary<string, object?>(row);
            var rt = (row["resource_type"] as string)?.ToLowerInvariant() ?? "";
            d["display_name"] = types.TryGetValue(rt, out var info) ? info.DisplayName : null;
            d["finops_category"] = types.TryGetValue(rt, out var i2) ? i2.ServiceCategory : null;
            result.Add(d);
        }
        return result;
    }

    // -------------------- DISCOVERY --------------------

    public async Task<IReadOnlyDictionary<string, object?>> DiscoverAsync(int clientId, CancellationToken ct = default)
    {
        var syncSummary = await sync.SyncAsync(clientId, ct);

        await using var conn = await factory.OpenAsync(ct);
        await EnsureDiscoverySchemaAsync(conn, ct);
        var (groups, _) = await ResolveSubscriptionGroupsAsync(conn, clientId, null, ct);
        var catalogTypes = CatalogTypeMap();

        var discovered = new Dictionary<string, (int Count, HashSet<string> Samples)>();
        foreach (var (credentialId, group) in groups)
        {
            var cred = await credentials.GetClientSecretCredentialAsync(credentialId, ct);
            var rows = await rg.RunQueryAsync(cred, group.Subscriptions.Keys.ToList(), DiscoveryKql, ct);
            foreach (var node in rows)
            {
                var row = new RgRow(node);
                var rt = (row.Str("resource_type") ?? row.Str("type") ?? "").ToLowerInvariant();
                if (string.IsNullOrEmpty(rt)) continue;
                if (!discovered.TryGetValue(rt, out var e)) e = discovered[rt] = (0, new HashSet<string>());
                var count = e.Count + (row.Int("resource_count") ?? row.Int("count_") ?? 0);
                foreach (var s in row.StrList("sample_names").Take(5)) e.Samples.Add(s);
                discovered[rt] = (count, e.Samples);
            }
        }

        var uncataloged = 0;
        foreach (var (rt, data) in discovered)
        {
            var catalogKey = catalogTypes.GetValueOrDefault(rt);
            var status = catalogKey is not null ? "cataloged" : "uncataloged";
            var sampleNames = string.Join(", ", data.Samples.OrderBy(x => x).Take(5));
            if (catalogKey is null) uncataloged++;

            var cls = catalogKey is not null
                ? new Classification(true, true, true, "Ya existe en el catalogo activo de la plataforma.")
                : ClassifyDiscoveredType(rt, data.Count, sampleNames);

            await UpsertDiscoveredAsync(conn, clientId, rt, data.Count, catalogKey, status, sampleNames, cls, ct);
        }

        return new Dictionary<string, object?>
        {
            ["message"] = "Descubrimiento completado",
            ["client_id"] = clientId,
            ["sync"] = syncSummary,
            ["total_types"] = discovered.Count,
            ["uncataloged"] = uncataloged,
        };
    }

    private Dictionary<string, string> CatalogTypeMap() =>
        catalog.ListServices(onlyActive: false)
            .Where(s => !string.IsNullOrEmpty(s.AzureResourceType))
            .GroupBy(s => s.AzureResourceType.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().ServiceKey);

    private sealed record Classification(bool OptimizationRelevant, bool RiApplicable, bool ShowInCatalog, string ReasonEs);

    private static Classification HeuristicClassify(string resourceType)
    {
        var rt = (resourceType ?? "").ToLowerInvariant();
        string[] riMarkers =
        [
            "microsoft.compute/virtualmachines", "microsoft.web/serverfarms", "microsoft.cache/redis",
            "microsoft.dbformysql/flexibleservers", "microsoft.dbforpostgresql/flexibleservers",
            "microsoft.sql/managedinstances", "microsoft.sql/servers/databases",
            "microsoft.documentdb/databaseaccounts", "microsoft.storage/storageaccounts",
        ];
        string[] noisy =
        [
            "microsoft.sql/managedinstances/databases", "/networkinterfaces", "/networksecuritygroups",
            "/privateendpoints", "/privatednszones", "/virtualnetworklinks", "alertsmanagement/",
            "/locks", "/diagnosticsettings", "/extensions", "/roleassignments", "/actiongroups",
        ];
        var isRi = riMarkers.Any(rt.Contains);
        var isNoisy = noisy.Any(rt.Contains);
        var show = isRi && !isNoisy;
        return new Classification(show, isRi, show,
            show ? "Candidato de optimizacion con posibilidad de RI o ahorro directo."
                 : "No se muestra por defecto porque no tiene RI directo ni ahorro recurrente claro.");
    }

    private Classification ClassifyDiscoveredType(string resourceType, int count, string sampleNames)
    {
        var fallback = HeuristicClassify(resourceType);
        if (!(config.AzureOpenAiEnabled && !string.IsNullOrWhiteSpace(config.AzureOpenAiEndpoint)
              && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiKey) && !string.IsNullOrWhiteSpace(config.AzureOpenAiDeployment)))
            return fallback;

        try
        {
            const string system =
                "Clasificas tipos de recursos Azure para una plataforma de optimizacion de costos. "
                + "No inventes precios. Responde solo JSON valido.";
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["resource_type"] = resourceType,
                ["resource_count"] = count,
                ["sample_names"] = sampleNames,
                ["decision"] = "Mostrar solo si el tipo de recurso tiene costo directo optimizable, Reservation/RI, savings plan, AHB/licenciamiento o una regla clara de ahorro. Ocultar auxiliares (NICs, NSG, private endpoints, alertas, locks, extensiones, objetos hijos).",
                ["schema"] = new Dictionary<string, object?>
                {
                    ["show_in_catalog"] = "boolean",
                    ["optimization_relevant"] = "boolean",
                    ["ri_applicable"] = "boolean",
                    ["ai_reason_es"] = "string corto en espanol",
                },
            });
            var raw = chat.Complete(system, payload);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            var t = raw.Trim();
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                t = t.Trim('`').Trim();
                if (t.StartsWith("json", StringComparison.OrdinalIgnoreCase)) t = t[4..].Trim();
            }
            using var doc = JsonDocument.Parse(t);
            var root = doc.RootElement;
            bool B(string k, bool def) => root.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;
            var reason = root.TryGetProperty("ai_reason_es", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString()! : fallback.ReasonEs;
            return new Classification(
                B("optimization_relevant", fallback.OptimizationRelevant),
                B("ri_applicable", fallback.RiApplicable),
                B("show_in_catalog", fallback.ShowInCatalog),
                reason.Length > 1000 ? reason[..1000] : reason);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discovery AI classification fallback resource_type={Rt} type={Type}", resourceType, ex.GetType().Name);
            return fallback;
        }
    }

    private static async Task UpsertDiscoveredAsync(
        SqlConnection conn, int clientId, string resourceType, int count, string? catalogKey, string status,
        string sampleNames, Classification cls, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF EXISTS (SELECT 1 FROM dbo.discovered_service_types WHERE client_id = @cid AND resource_type = @rt)
                UPDATE dbo.discovered_service_types
                SET resource_count = @cnt, catalog_service_key = @ck,
                    status = CASE WHEN status = 'ignored' THEN status ELSE @st END,
                    sample_names = @sn, optimization_relevant = @opt, ri_applicable = @ri,
                    show_in_catalog = @show, ai_reason_es = @reason,
                    ai_classified_at = SYSUTCDATETIME(), last_seen_at = SYSUTCDATETIME()
                WHERE client_id = @cid AND resource_type = @rt;
            ELSE
                INSERT INTO dbo.discovered_service_types
                    (client_id, resource_type, resource_count, catalog_service_key, status, sample_names,
                     optimization_relevant, ri_applicable, show_in_catalog, ai_reason_es, ai_classified_at)
                VALUES (@cid, @rt, @cnt, @ck, @st, @sn, @opt, @ri, @show, @reason, SYSUTCDATETIME());
            """;
        void P(string n, object? v) => cmd.Parameters.Add(new SqlParameter(n, v ?? DBNull.Value));
        P("@cid", clientId); P("@rt", resourceType); P("@cnt", count);
        P("@ck", catalogKey); P("@st", status); P("@sn", sampleNames);
        P("@opt", cls.OptimizationRelevant ? 1 : 0); P("@ri", cls.RiApplicable ? 1 : 0);
        P("@show", cls.ShowInCatalog ? 1 : 0); P("@reason", cls.ReasonEs);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListDiscoveredAsync(
        int clientId, bool onlyUncataloged, bool onlyRelevant, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureDiscoverySchemaAsync(conn, ct);
        var where = "WHERE client_id = @cid";
        if (onlyUncataloged) where += " AND status = 'uncataloged'";
        if (onlyRelevant) where += " AND (catalog_service_key IS NOT NULL OR show_in_catalog = 1)";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT discovery_id, client_id, resource_type, resource_count, catalog_service_key, status,
                   sample_names, optimization_relevant, ri_applicable, show_in_catalog, ai_reason_es,
                   ai_classified_at, first_seen_at, last_seen_at
            FROM dbo.discovered_service_types {where}
            ORDER BY CASE WHEN status = 'uncataloged' THEN 0 ELSE 1 END, resource_count DESC, resource_type
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var list = new List<IReadOnlyDictionary<string, object?>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        bool? B(int i) => r.IsDBNull(i) ? null : r.GetBoolean(i);
        string? S(int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();
        while (await r.ReadAsync(ct))
            list.Add(new Dictionary<string, object?>
            {
                ["discovery_id"] = r.GetInt32(0), ["client_id"] = r.GetInt32(1), ["resource_type"] = r.GetString(2),
                ["resource_count"] = r.GetInt32(3), ["catalog_service_key"] = r.IsDBNull(4) ? null : r.GetString(4),
                ["status"] = r.GetString(5), ["sample_names"] = r.IsDBNull(6) ? null : r.GetString(6),
                ["optimization_relevant"] = B(7), ["ri_applicable"] = B(8), ["show_in_catalog"] = B(9),
                ["ai_reason_es"] = r.IsDBNull(10) ? null : r.GetString(10), ["ai_classified_at"] = S(11),
                ["first_seen_at"] = S(12), ["last_seen_at"] = S(13),
            });
        return list;
    }

    private static async Task EnsureDiscoverySchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.discovered_service_types', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.discovered_service_types (
                    discovery_id INT IDENTITY(1,1) PRIMARY KEY,
                    client_id INT NOT NULL, resource_type NVARCHAR(300) NOT NULL,
                    resource_count INT NOT NULL DEFAULT 0, catalog_service_key NVARCHAR(100) NULL,
                    status NVARCHAR(40) NOT NULL DEFAULT 'uncataloged', sample_names NVARCHAR(MAX) NULL,
                    first_seen_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    last_seen_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT UQ_discovered_service_types UNIQUE (client_id, resource_type)
                );
            END
            IF COL_LENGTH('dbo.discovered_service_types', 'optimization_relevant') IS NULL ALTER TABLE dbo.discovered_service_types ADD optimization_relevant BIT NULL;
            IF COL_LENGTH('dbo.discovered_service_types', 'ri_applicable') IS NULL ALTER TABLE dbo.discovered_service_types ADD ri_applicable BIT NULL;
            IF COL_LENGTH('dbo.discovered_service_types', 'show_in_catalog') IS NULL ALTER TABLE dbo.discovered_service_types ADD show_in_catalog BIT NULL;
            IF COL_LENGTH('dbo.discovered_service_types', 'ai_reason_es') IS NULL ALTER TABLE dbo.discovered_service_types ADD ai_reason_es NVARCHAR(MAX) NULL;
            IF COL_LENGTH('dbo.discovered_service_types', 'ai_classified_at') IS NULL ALTER TABLE dbo.discovered_service_types ADD ai_classified_at DATETIME2 NULL;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
