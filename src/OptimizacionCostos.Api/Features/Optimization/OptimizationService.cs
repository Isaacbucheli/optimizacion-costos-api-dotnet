using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.AzureIntegration;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Features.Optimization;

public sealed class NoManagedSubscriptionsException() : Exception("El cliente no tiene suscripciones administradas activas.");

/// <summary>
/// Barrido de optimización de tenant. Port de app/optimization/*.py + routes/optimization.py.
/// Corre los 13 checks por suscripción vía Resource Graph, estima ahorro, persiste hallazgos y
/// reconcilia su estado entre barridos. Solo lectura de Azure.
/// </summary>
public interface IOptimizationService
{
    bool AccessAllowed(string? email);
    Task<IReadOnlyDictionary<string, object?>> RunScanAsync(int clientId, string? actor, CancellationToken ct = default);
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListScansAsync(int clientId, CancellationToken ct = default);
    Task<int?> ScanOwnerAsync(int scanId, CancellationToken ct = default);
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ScanFindingsAsync(int scanId, CancellationToken ct = default);
    Task<int?> FindingStateOwnerAsync(byte[] fingerprint, CancellationToken ct = default);
    Task<bool> UpdateStateAsync(byte[] fingerprint, string state, string? notes, string? actor, CancellationToken ct = default);
    Task EnsureSchemaAsync(CancellationToken ct = default);
}

public sealed class OptimizationService(
    ISqlConnectionFactory factory, IResourceGraphRunner rg, IAzureCredentialFactory credentials,
    ICostEstimation cost, IRightSizingAnalyzer rightSizing, AppConfig config, ILogger<OptimizationService> logger) : IOptimizationService
{
    public bool AccessAllowed(string? email)
    {
        if (config.OptimizationAllowedEmails.Length == 0) return true; // lista vacía = abierto
        return !string.IsNullOrWhiteSpace(email)
            && config.OptimizationAllowedEmails.Any(e => string.Equals(e, email.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
    }

    public async Task<IReadOnlyDictionary<string, object?>> RunScanAsync(int clientId, string? actor, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        var groups = await ManagedSubscriptionsAsync(conn, clientId, ct);
        if (groups.Count == 0) throw new NoManagedSubscriptionsException();

        var scanId = await CreateScanAsync(conn, clientId, actor, ct);
        try
        {
            var allFindings = new List<Finding>();
            var allErrors = new List<object>();
            var subsScanned = 0;

            foreach (var (credentialId, subIds) in groups)
            {
                Azure.Core.TokenCredential cred;
                try { cred = await credentials.GetClientSecretCredentialAsync(credentialId, ct); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "scan {Scan}: credencial {Cred} no disponible", scanId, credentialId);
                    allErrors.Add(new { check_id = "credential_or_subscription", credential_id = credentialId, error = ex.GetType().Name });
                    continue;
                }
                foreach (var subId in subIds)
                {
                    try
                    {
                        foreach (var check in OptimizationChecks.Registered)
                        {
                            try
                            {
                                var nodes = await rg.RunQueryAsync(cred, [subId], check.ArgQuery, ct);
                                var rows = nodes.Select(n => new RgRow(n)).ToList();
                                allFindings.AddRange(check.BuildFindings(check, rows, subId, cost));
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "check falló check_id={Check} sub={Sub}", check.CheckId, subId);
                                allErrors.Add(new { check_id = check.CheckId, error = ex.GetType().Name });
                            }
                        }
                        // Right-sizing (Grupo B): métricas de Azure Monitor. Best-effort — un fallo (p.ej. sin
                        // Monitoring Reader) no aborta el barrido ni pierde los hallazgos ARG ya recolectados.
                        try
                        {
                            allFindings.AddRange(await rightSizing.AnalyzeAsync(cred, subId, ct));
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "right-sizing falló sub={Sub}", subId);
                            allErrors.Add(new { check_id = RightSizing.CheckId, error = ex.GetType().Name });
                        }
                        subsScanned++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "scan {Scan}: falló suscripción {Sub}", scanId, subId);
                        allErrors.Add(new { check_id = "credential_or_subscription", subscription_id = subId, error = ex.GetType().Name });
                    }
                }
            }

            // Persistir + reconciliar + finalizar en transacción.
            IReadOnlyDictionary<string, object?> summary;
            await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct))
            {
                await SaveFindingsAsync(conn, tx, clientId, scanId, allFindings, ct);
                var recon = await ReconcileAsync(conn, tx, clientId, scanId, allFindings, ct);
                summary = await FinalizeScanAsync(conn, tx, scanId, subsScanned, allFindings, allErrors, recon, ct);
                await tx.CommitAsync(ct);
            }
            return summary;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "scan falló client_id={Cid}", clientId);
            try { await MarkScanFailedAsync(conn, scanId, ct); } catch { /* no ocultar el error original */ }
            throw;
        }
    }

    // -------------------- scans / findings / state --------------------

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListScansAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT scan_id, started_at, finished_at, status, subscriptions_scanned,
                   findings_count, total_estimated_monthly_savings, currency
            FROM dbo.optimization_scan WHERE client_id = @cid ORDER BY started_at DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var list = new List<IReadOnlyDictionary<string, object?>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Dictionary<string, object?>
            {
                ["scan_id"] = r.GetInt32(0), ["started_at"] = r.GetDateTime(1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                ["finished_at"] = r.IsDBNull(2) ? null : r.GetDateTime(2).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), ["status"] = r.GetString(3),
                ["subscriptions_scanned"] = r.GetInt32(4), ["findings_count"] = r.GetInt32(5),
                ["total_estimated_monthly_savings"] = r.IsDBNull(6) ? null : Convert.ToDouble(r.GetValue(6)),
                ["currency"] = r.GetString(7),
            });
        return list;
    }

    public async Task<int?> ScanOwnerAsync(int scanId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_id FROM dbo.optimization_scan WHERE scan_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", scanId));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToInt32(v);
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ScanFindingsAsync(int scanId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.check_id, f.category, f.severity, f.subscription_id, f.azure_resource_id,
                   f.resource_name, f.resource_type, f.region, f.details_json,
                   f.estimated_monthly_savings, f.currency, f.fingerprint,
                   COALESCE(st.state, 'abierto') AS state, st.notes
            FROM dbo.optimization_finding f
            LEFT JOIN dbo.optimization_finding_state st ON st.fingerprint = f.fingerprint
            WHERE f.scan_id = @id ORDER BY f.estimated_monthly_savings DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@id", scanId));
        var list = new List<IReadOnlyDictionary<string, object?>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Dictionary<string, object?>
            {
                ["check_id"] = r.GetString(0), ["category"] = r.GetString(1), ["severity"] = r.GetString(2),
                ["subscription_id"] = r.GetString(3), ["azure_resource_id"] = r.GetString(4),
                ["resource_name"] = r.IsDBNull(5) ? null : r.GetString(5), ["resource_type"] = r.IsDBNull(6) ? null : r.GetString(6),
                ["region"] = r.IsDBNull(7) ? null : r.GetString(7),
                ["details"] = r.IsDBNull(8) ? new Dictionary<string, object?>() : JsonSerializer.Deserialize<JsonElement>(r.GetString(8)),
                ["estimated_monthly_savings"] = r.IsDBNull(9) ? null : Convert.ToDouble(r.GetValue(9)),
                ["currency"] = r.GetString(10), ["fingerprint"] = Convert.ToHexString((byte[])r.GetValue(11)).ToLowerInvariant(),
                ["state"] = r.GetString(12), ["notes"] = r.IsDBNull(13) ? null : r.GetString(13),
            });
        return list;
    }

    public async Task<int?> FindingStateOwnerAsync(byte[] fingerprint, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_id FROM dbo.optimization_finding_state WHERE fingerprint = @fp";
        cmd.Parameters.Add(new SqlParameter("@fp", fingerprint));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToInt32(v);
    }

    internal const string UpdateStateSql = """
        UPDATE dbo.optimization_finding_state
        SET state = @state, notes = @notes, updated_by = @actor, updated_at = SYSUTCDATETIME(),
            resolved_by_kind = CASE
                WHEN @state = 'resuelto' THEN 'manual'
                WHEN @state IN ('abierto', 'en_progreso') THEN NULL
                ELSE resolved_by_kind END
        WHERE fingerprint = @fp
        """;

    public async Task<bool> UpdateStateAsync(byte[] fingerprint, string state, string? notes, string? actor, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = UpdateStateSql;
        cmd.Parameters.Add(new SqlParameter("@state", state));
        cmd.Parameters.Add(new SqlParameter("@notes", (object?)notes ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@actor", (object?)actor ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@fp", fingerprint));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // -------------------- persistencia interna --------------------

    private static async Task<Dictionary<int, List<string>>> ManagedSubscriptionsAsync(SqlConnection conn, int clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.credential_id, s.subscription_id
            FROM dbo.client_azure_subscriptions s
            INNER JOIN dbo.client_azure_credentials c ON s.credential_id = c.credential_id
            WHERE s.client_id = @cid AND s.is_active = 1 AND COALESCE(s.is_managed, 1) = 1 AND c.is_active = 1
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var groups = new Dictionary<int, List<string>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (!groups.TryGetValue(r.GetInt32(0), out var l)) l = groups[r.GetInt32(0)] = [];
            l.Add(r.GetString(1));
        }
        return groups;
    }

    private static async Task<int> CreateScanAsync(SqlConnection conn, int clientId, string? actor, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO dbo.optimization_scan (client_id, status, triggered_by) OUTPUT INSERTED.scan_id VALUES (@cid, 'running', @by)";
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@by", (object?)actor ?? DBNull.Value));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task SaveFindingsAsync(SqlConnection conn, SqlTransaction tx, int clientId, int scanId, List<Finding> findings, CancellationToken ct)
    {
        foreach (var f in findings)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO dbo.optimization_finding
                    (scan_id, client_id, check_id, category, severity, subscription_id, azure_resource_id,
                     resource_name, resource_type, region, details_json, estimated_monthly_savings, currency, fingerprint)
                VALUES (@scan, @cid, @check, @cat, @sev, @sub, @rid, @rname, @rtype, @region, @details, @savings, @ccy, @fp)
                """;
            void P(string n, object? v) => cmd.Parameters.Add(new SqlParameter(n, v ?? DBNull.Value));
            P("@scan", scanId); P("@cid", clientId); P("@check", f.CheckId); P("@cat", f.Category); P("@sev", f.Severity);
            P("@sub", f.SubscriptionId); P("@rid", f.AzureResourceId); P("@rname", f.ResourceName); P("@rtype", f.ResourceType);
            P("@region", f.Region); P("@details", JsonSerializer.Serialize(f.Details));
            P("@savings", f.EstimatedMonthlySavings); P("@ccy", f.Currency); P("@fp", f.Fingerprint(clientId));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    internal const string AutoResolveSql =
        "UPDATE dbo.optimization_finding_state SET state = 'resuelto', resolved_by_kind = 'auto', updated_at = SYSUTCDATETIME() WHERE fingerprint = @fp";

    private static async Task<Dictionary<string, int>> ReconcileAsync(SqlConnection conn, SqlTransaction tx, int clientId, int scanId, List<Finding> findings, CancellationToken ct)
    {
        var currentByHex = new Dictionary<string, byte[]>();
        foreach (var f in findings)
        {
            var fp = f.Fingerprint(clientId);
            currentByHex[Convert.ToHexString(fp)] = fp;
        }

        var existing = new Dictionary<string, string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT fingerprint, state FROM dbo.optimization_finding_state WHERE client_id = @cid";
            cmd.Parameters.Add(new SqlParameter("@cid", clientId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                existing[Convert.ToHexString((byte[])r.GetValue(0))] = r.GetString(1);
        }

        var counts = new Dictionary<string, int> { ["new"] = 0, ["persisting"] = 0, ["auto_resolved"] = 0 };
        foreach (var (hex, fp) in currentByHex)
        {
            if (existing.ContainsKey(hex))
            {
                await ExecFpAsync(conn, tx, "UPDATE dbo.optimization_finding_state SET last_seen_scan_id = @scan, updated_at = SYSUTCDATETIME() WHERE fingerprint = @fp", fp, scanId, ct);
                counts["persisting"]++;
            }
            else
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO dbo.optimization_finding_state
                        (fingerprint, client_id, check_id, azure_resource_id, state, first_seen_scan_id, last_seen_scan_id)
                    SELECT TOP 1 @fp, @cid, check_id, azure_resource_id, 'abierto', @scan, @scan
                    FROM dbo.optimization_finding WHERE fingerprint = @fp AND scan_id = @scan
                    """;
                cmd.Parameters.Add(new SqlParameter("@fp", fp));
                cmd.Parameters.Add(new SqlParameter("@cid", clientId));
                cmd.Parameters.Add(new SqlParameter("@scan", scanId));
                await cmd.ExecuteNonQueryAsync(ct);
                counts["new"]++;
            }
        }
        foreach (var (hex, state) in existing)
        {
            if (!currentByHex.ContainsKey(hex) && state is "abierto" or "en_progreso")
            {
                await ExecFpAsync(conn, tx, AutoResolveSql,
                    Convert.FromHexString(hex), 0, ct, includeScan: false);
                counts["auto_resolved"]++;
            }
        }
        return counts;
    }

    private static async Task ExecFpAsync(SqlConnection conn, SqlTransaction tx, string sql, byte[] fp, int scanId, CancellationToken ct, bool includeScan = true)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@fp", fp));
        if (includeScan) cmd.Parameters.Add(new SqlParameter("@scan", scanId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyDictionary<string, object?>> FinalizeScanAsync(
        SqlConnection conn, SqlTransaction tx, int scanId, int subsScanned, List<Finding> findings, List<object> errors, Dictionary<string, int> recon, CancellationToken ct)
    {
        var total = findings.Count > 0 ? Math.Round(findings.Where(f => f.EstimatedMonthlySavings is not null).Sum(f => f.EstimatedMonthlySavings!.Value), 2) : 0.0;
        var errorSummary = errors.Count > 0 ? JsonSerializer.Serialize(errors) : null;
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE dbo.optimization_scan
            SET status = 'completed', finished_at = SYSUTCDATETIME(), subscriptions_scanned = @subs,
                findings_count = @count, total_estimated_monthly_savings = @total, error_summary = @errs
            WHERE scan_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@subs", subsScanned));
        cmd.Parameters.Add(new SqlParameter("@count", findings.Count));
        cmd.Parameters.Add(new SqlParameter("@total", total));
        cmd.Parameters.Add(new SqlParameter("@errs", (object?)errorSummary ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@id", scanId));
        await cmd.ExecuteNonQueryAsync(ct);

        return new Dictionary<string, object?>
        {
            ["scan_id"] = scanId, ["findings_count"] = findings.Count, ["subscriptions_scanned"] = subsScanned,
            ["total_estimated_monthly_savings"] = total, ["errors"] = errors,
            ["new"] = recon["new"], ["persisting"] = recon["persisting"], ["auto_resolved"] = recon["auto_resolved"],
        };
    }

    private static async Task MarkScanFailedAsync(SqlConnection conn, int scanId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dbo.optimization_scan SET status = 'failed', finished_at = SYSUTCDATETIME(), error_summary = @e WHERE scan_id = @id";
        cmd.Parameters.Add(new SqlParameter("@e", "El barrido no pudo completarse."));
        cmd.Parameters.Add(new SqlParameter("@id", scanId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal const string SchemaSql = """
        IF OBJECT_ID('dbo.optimization_scan', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.optimization_scan (
                scan_id INT IDENTITY(1,1) PRIMARY KEY, client_id INT NOT NULL,
                started_at DATETIME2 NOT NULL CONSTRAINT DF_optscan_started DEFAULT SYSUTCDATETIME(),
                finished_at DATETIME2 NULL, status VARCHAR(20) NOT NULL CONSTRAINT DF_optscan_status DEFAULT 'running',
                subscriptions_scanned INT NOT NULL CONSTRAINT DF_optscan_subs DEFAULT 0,
                findings_count INT NOT NULL CONSTRAINT DF_optscan_count DEFAULT 0,
                total_estimated_monthly_savings DECIMAL(18,2) NULL,
                currency VARCHAR(10) NOT NULL CONSTRAINT DF_optscan_ccy DEFAULT 'USD',
                triggered_by NVARCHAR(255) NULL, error_summary NVARCHAR(MAX) NULL,
                CONSTRAINT FK_optscan_client FOREIGN KEY (client_id) REFERENCES dbo.clients(client_id));
            CREATE INDEX IX_optscan_client_started ON dbo.optimization_scan(client_id, started_at DESC);
        END;
        IF OBJECT_ID('dbo.optimization_finding', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.optimization_finding (
                finding_id INT IDENTITY(1,1) PRIMARY KEY, scan_id INT NOT NULL, client_id INT NOT NULL,
                check_id VARCHAR(100) NOT NULL, category VARCHAR(50) NOT NULL, severity VARCHAR(20) NOT NULL,
                subscription_id VARCHAR(100) NOT NULL, azure_resource_id NVARCHAR(2048) NOT NULL,
                resource_name NVARCHAR(512) NULL, resource_type NVARCHAR(300) NULL, region VARCHAR(100) NULL,
                details_json NVARCHAR(MAX) NULL, estimated_monthly_savings DECIMAL(18,2) NULL,
                currency VARCHAR(10) NOT NULL CONSTRAINT DF_optfind_ccy DEFAULT 'USD', fingerprint VARBINARY(32) NOT NULL,
                CONSTRAINT FK_optfind_scan FOREIGN KEY (scan_id) REFERENCES dbo.optimization_scan(scan_id));
            CREATE INDEX IX_optfind_scan ON dbo.optimization_finding(scan_id);
            CREATE INDEX IX_optfind_fingerprint ON dbo.optimization_finding(fingerprint);
        END;
        IF OBJECT_ID('dbo.optimization_finding_state', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.optimization_finding_state (
                fingerprint VARBINARY(32) NOT NULL PRIMARY KEY, client_id INT NOT NULL, check_id VARCHAR(100) NOT NULL,
                azure_resource_id NVARCHAR(2048) NOT NULL, state VARCHAR(20) NOT NULL CONSTRAINT DF_optstate_state DEFAULT 'abierto',
                notes NVARCHAR(MAX) NULL, first_seen_scan_id INT NOT NULL, last_seen_scan_id INT NOT NULL,
                updated_by NVARCHAR(255) NULL, updated_at DATETIME2 NOT NULL CONSTRAINT DF_optstate_updated DEFAULT SYSUTCDATETIME(),
                CONSTRAINT FK_optstate_client FOREIGN KEY (client_id) REFERENCES dbo.clients(client_id),
                CONSTRAINT CK_optstate_state CHECK (state IN ('abierto','en_progreso','resuelto','ignorado')));
            CREATE INDEX IX_optstate_client ON dbo.optimization_finding_state(client_id);
        END;
        IF COL_LENGTH('dbo.optimization_finding_state', 'resolved_by_kind') IS NULL
            ALTER TABLE dbo.optimization_finding_state ADD resolved_by_kind VARCHAR(20) NULL;
        """;

    private static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
