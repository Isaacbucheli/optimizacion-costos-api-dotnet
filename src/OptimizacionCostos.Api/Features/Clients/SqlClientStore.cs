using System.Globalization;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Clients;

public sealed record ClientListItem(
    int ClientId, string ClientName, string? TaxId, string? ContactName, string? ContactEmail,
    bool IsActive, string? CreatedAt, bool HasLogo);

/// <summary>
/// Acceso a dbo.clients + purga/borrado en cascada. Port de los accesos SQL de
/// app/routes/clients.py y app/client_purge.py. SQL siempre parametrizado; los DELETE de purga
/// corren en una transacción (hijos→padres) para respetar FKs.
/// </summary>
public interface IClientStore
{
    Task<IReadOnlyList<ClientListItem>> ListAsync(CancellationToken ct = default);
    Task<int> CreateAsync(string clientName, string? taxId, string? contactName, string? contactEmail, string? notes, CancellationToken ct = default);
    Task<bool> ExistsAsync(int clientId, CancellationToken ct = default);
    Task<bool> NameExistsAsync(string name, int excludeClientId, CancellationToken ct = default);
    Task<bool> RenameAsync(int clientId, string name, CancellationToken ct = default);
    Task<string?> GetNameAsync(int clientId, CancellationToken ct = default);
    Task<(string Name, string? LogoBlobName)?> GetNameAndLogoAsync(int clientId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> PurgeDataAsync(int clientId, CancellationToken ct = default);
    Task<(IReadOnlyDictionary<string, int> Counts, string? LogoBlobName)> DeleteClientCascadeAsync(int clientId, CancellationToken ct = default);
    Task UpdateLogoMetaAsync(int clientId, string blobName, string contentType, CancellationToken ct = default);
    Task<(string? BlobName, string? ContentType)?> GetLogoAsync(int clientId, CancellationToken ct = default);
    Task ClearLogoAsync(int clientId, CancellationToken ct = default);
    Task<(bool Managed, string? Note)> GetSecurityManagementAsync(int clientId, CancellationToken ct = default);
    Task SetSecurityManagementAsync(int clientId, bool managed, string? note, CancellationToken ct = default);
}

public sealed class SqlClientStore(ISqlConnectionFactory factory) : IClientStore
{
    // Port de KNOWN_DETAIL_TABLES (app/inventory_inserters.py). Allowlist fija → SQL seguro.
    private static readonly string[] DetailTables =
    [
        "vm_details", "disk_details", "sql_db_details", "appservice_plan_details",
        "mysql_flex_details", "cosmos_details", "redis_details", "public_ip_details",
        "sql_vm_details", "sql_managed_instance_details", "elastic_pool_details",
    ];

    private static string? D(SqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetValue(i) is DateTime dt
            ? dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
            : r.GetValue(i).ToString();

    private static async Task EnsureLogoSchemaAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            IF COL_LENGTH('dbo.clients', 'logo_blob_name') IS NULL
                ALTER TABLE dbo.clients ADD logo_blob_name NVARCHAR(400) NULL;
            IF COL_LENGTH('dbo.clients', 'logo_content_type') IS NULL
                ALTER TABLE dbo.clients ADD logo_content_type NVARCHAR(100) NULL;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ClientListItem>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureLogoSchemaAsync(conn, null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT client_id, client_name, tax_id, contact_name, contact_email,
                   is_active, created_at, logo_blob_name
            FROM dbo.clients ORDER BY client_name
            """;
        var list = new List<ClientListItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ClientListItem(
                r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                !r.IsDBNull(5) && r.GetBoolean(5), D(r, 6), !r.IsDBNull(7) && !string.IsNullOrEmpty(r.GetString(7))));
        return list;
    }

    public async Task<int> CreateAsync(string clientName, string? taxId, string? contactName, string? contactEmail, string? notes, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.clients (client_name, tax_id, contact_name, contact_email, notes)
            OUTPUT INSERTED.client_id
            VALUES (@name, @tax, @cn, @ce, @notes)
            """;
        cmd.Parameters.Add(new SqlParameter("@name", clientName));
        cmd.Parameters.Add(new SqlParameter("@tax", (object?)taxId ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cn", (object?)contactName ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@ce", (object?)contactEmail ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@notes", (object?)notes ?? DBNull.Value));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<bool> ExistsAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dbo.clients WHERE client_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", clientId));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<bool> NameExistsAsync(string name, int excludeClientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dbo.clients WHERE client_name = @name AND client_id <> @id";
        cmd.Parameters.Add(new SqlParameter("@name", name));
        cmd.Parameters.Add(new SqlParameter("@id", excludeClientId));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<bool> RenameAsync(int clientId, string name, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dbo.clients SET client_name = @name WHERE client_id = @id";
        cmd.Parameters.Add(new SqlParameter("@name", name));
        cmd.Parameters.Add(new SqlParameter("@id", clientId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<string?> GetNameAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_name FROM dbo.clients WHERE client_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", clientId));
        return (await cmd.ExecuteScalarAsync(ct)) as string;
    }

    public async Task<(string Name, string? LogoBlobName)?> GetNameAndLogoAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureLogoSchemaAsync(conn, null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_name, logo_blob_name FROM dbo.clients WHERE client_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", clientId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    public async Task<IReadOnlyDictionary<string, int>> PurgeDataAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var counts = new Dictionary<string, int>();
        await PurgeCoreAsync(conn, tx, clientId, counts, ct);
        await tx.CommitAsync(ct);
        return counts;
    }

    public async Task<(IReadOnlyDictionary<string, int> Counts, string? LogoBlobName)> DeleteClientCascadeAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureLogoSchemaAsync(conn, null, ct);

        string? logoBlobName;
        await using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT logo_blob_name FROM dbo.clients WHERE client_id = @id";
            find.Parameters.Add(new SqlParameter("@id", clientId));
            logoBlobName = (await find.ExecuteScalarAsync(ct)) as string;
        }

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var counts = new Dictionary<string, int>();
        await PurgeCoreAsync(conn, tx, clientId, counts, ct);
        // Borrados extra del delete_client (cascada de configuración del cliente).
        await CountedDeleteAsync(conn, tx, counts, "discovered_service_types",
            "IF OBJECT_ID('dbo.discovered_service_types','U') IS NOT NULL DELETE FROM dbo.discovered_service_types WHERE client_id = @id;", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "user_client_assignment",
            "DELETE FROM dbo.user_client_assignment WHERE client_id = @id", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "client_azure_subscriptions",
            "DELETE FROM dbo.client_azure_subscriptions WHERE client_id = @id", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "client_azure_credentials",
            "DELETE FROM dbo.client_azure_credentials WHERE client_id = @id", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "clients",
            "DELETE FROM dbo.clients WHERE client_id = @id", clientId, ct);
        await tx.CommitAsync(ct);
        return (counts, logoBlobName);
    }

    // Port de purge_client_data (hijos→padres). Acumula rowcounts por etiqueta.
    private static async Task PurgeCoreAsync(SqlConnection conn, SqlTransaction tx, int clientId, Dictionary<string, int> counts, CancellationToken ct)
    {
        // -------- WAF --------
        await CountedDeleteAsync(conn, tx, counts, "waf_resource_finding", """
            DELETE f FROM dbo.waf_resource_finding f
            INNER JOIN dbo.waf_recommendation r ON r.recommendation_id = f.recommendation_id
            WHERE r.client_id = @id
            """, clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "waf_tracking_history", "DELETE FROM dbo.waf_tracking_history WHERE client_id = @id", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "waf_recommendation_comment", "DELETE FROM dbo.waf_recommendation_comment WHERE client_id = @id", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "waf_recommendation_tracking", "DELETE FROM dbo.waf_recommendation_tracking WHERE client_id = @id", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "waf_recommendation", "DELETE FROM dbo.waf_recommendation WHERE client_id = @id", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "waf_ingestion_run", "DELETE FROM dbo.waf_ingestion_run WHERE client_id = @id", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "waf_advisor_score_snapshot",
            "IF OBJECT_ID('dbo.waf_advisor_score_snapshot','U') IS NOT NULL DELETE FROM dbo.waf_advisor_score_snapshot WHERE client_id = @id;", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "waf_canonical_alias", """
            DELETE a FROM dbo.waf_canonical_alias a
            WHERE NOT EXISTS (SELECT 1 FROM dbo.waf_recommendation r WHERE r.canonical_id = a.canonical_id)
            """, null, ct);
        await CountedDeleteAsync(conn, tx, counts, "waf_recommendation_canonical", """
            DELETE c FROM dbo.waf_recommendation_canonical c
            WHERE NOT EXISTS (SELECT 1 FROM dbo.waf_recommendation r WHERE r.canonical_id = c.canonical_id)
            """, null, ct);

        // -------- Informes mensuales --------
        await CountedDeleteAsync(conn, tx, counts, "report_action_plan", "DELETE FROM dbo.report_action_plan WHERE client_id = @id", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "client_monthly_report", "DELETE FROM dbo.client_monthly_report WHERE client_id = @id", clientId, ct);

        // -------- Costos e inventario --------
        await CountedDeleteAsync(conn, tx, counts, "scenario_breakdown", """
            DELETE FROM dbo.scenario_breakdown WHERE scenario_id IN (
                SELECT s.scenario_id FROM dbo.cost_scenarios s
                INNER JOIN dbo.cost_analysis a ON a.analysis_id = s.analysis_id WHERE a.client_id = @id)
            """, clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "cost_scenarios",
            "DELETE FROM dbo.cost_scenarios WHERE analysis_id IN (SELECT analysis_id FROM dbo.cost_analysis WHERE client_id = @id)", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "cost_results",
            "DELETE FROM dbo.cost_results WHERE analysis_id IN (SELECT analysis_id FROM dbo.cost_analysis WHERE client_id = @id)", clientId, ct);
        foreach (var table in DetailTables)
            await CountedDeleteAsync(conn, tx, counts, table, $"""
                IF OBJECT_ID('dbo.{table}','U') IS NOT NULL
                    DELETE FROM dbo.{table} WHERE resource_id IN (
                        SELECT r.resource_id FROM dbo.azure_resources r
                        INNER JOIN dbo.cost_analysis a ON a.analysis_id = r.analysis_id WHERE a.client_id = @id);
                """, clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "azure_resources",
            "DELETE FROM dbo.azure_resources WHERE analysis_id IN (SELECT analysis_id FROM dbo.cost_analysis WHERE client_id = @id)", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "analysis_files",
            "DELETE FROM dbo.analysis_files WHERE analysis_id IN (SELECT analysis_id FROM dbo.cost_analysis WHERE client_id = @id)", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "cost_analysis", "DELETE FROM dbo.cost_analysis WHERE client_id = @id", clientId, ct);

        // -------- Advisor score: histórico y trabajos de sincronización --------
        // Las dos tienen FK a clients y faltaban acá: borrar un cliente con histórico de score o
        // con un trabajo de sync encolado fallaba por clave foránea.
        await CountedDeleteAsync(conn, tx, counts, "waf_advisor_score_history",
            "IF OBJECT_ID('dbo.waf_advisor_score_history','U') IS NOT NULL DELETE FROM dbo.waf_advisor_score_history WHERE client_id = @id;", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "waf_advisor_sync_job",
            "IF OBJECT_ID('dbo.waf_advisor_sync_job','U') IS NOT NULL DELETE FROM dbo.waf_advisor_sync_job WHERE client_id = @id;", clientId, ct);

        // -------- Barrido de optimización de Azure --------
        // optimization_finding_state va primero: además de su FK a clients tiene FK_optfind_scan
        // contra optimization_scan, así que al revés falla aunque el cliente exista.
        await CountedDeleteAsync(conn, tx, counts, "optimization_finding_state",
            "IF OBJECT_ID('dbo.optimization_finding_state','U') IS NOT NULL DELETE FROM dbo.optimization_finding_state WHERE client_id = @id;", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "optimization_scan",
            "IF OBJECT_ID('dbo.optimization_scan','U') IS NOT NULL DELETE FROM dbo.optimization_scan WHERE client_id = @id;", clientId, ct);

        // -------- Informe de valor --------
        // Los tres insumos antes de la bitácora, por la convención hijos→padres de este método
        // (entre ellas no hay FK: ingesta_id no declara REFERENCES).
        await CountedDeleteAsync(conn, tx, counts, "informe_valor_facturacion",
            "IF OBJECT_ID('dbo.informe_valor_facturacion','U') IS NOT NULL DELETE FROM dbo.informe_valor_facturacion WHERE client_id = @id;", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "informe_valor_caso",
            "IF OBJECT_ID('dbo.informe_valor_caso','U') IS NOT NULL DELETE FROM dbo.informe_valor_caso WHERE client_id = @id;", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "informe_valor_rbac",
            "IF OBJECT_ID('dbo.informe_valor_rbac','U') IS NOT NULL DELETE FROM dbo.informe_valor_rbac WHERE client_id = @id;", clientId, ct);
        await CountedDeleteAsync(conn, tx, counts, "informe_valor_ingesta",
            "IF OBJECT_ID('dbo.informe_valor_ingesta','U') IS NOT NULL DELETE FROM dbo.informe_valor_ingesta WHERE client_id = @id;", clientId, ct);
    }

    private static async Task CountedDeleteAsync(
        SqlConnection conn, SqlTransaction tx, Dictionary<string, int> counts, string label, string sql, int? clientId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        if (clientId is not null) cmd.Parameters.Add(new SqlParameter("@id", clientId.Value));
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        counts[label] = counts.GetValueOrDefault(label) + (rows > 0 ? rows : 0);
    }

    public async Task UpdateLogoMetaAsync(int clientId, string blobName, string contentType, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureLogoSchemaAsync(conn, null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dbo.clients SET logo_blob_name = @b, logo_content_type = @ct WHERE client_id = @id";
        cmd.Parameters.Add(new SqlParameter("@b", blobName));
        cmd.Parameters.Add(new SqlParameter("@ct", contentType));
        cmd.Parameters.Add(new SqlParameter("@id", clientId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(string? BlobName, string? ContentType)?> GetLogoAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureLogoSchemaAsync(conn, null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT logo_blob_name, logo_content_type FROM dbo.clients WHERE client_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", clientId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    public async Task ClearLogoAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureLogoSchemaAsync(conn, null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dbo.clients SET logo_blob_name = NULL, logo_content_type = NULL WHERE client_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", clientId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- Gestión externa de seguridad (Gestión de Vulnerabilidades) ----------

    private static async Task EnsureSecurityMgmtSchemaAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            IF COL_LENGTH('dbo.clients', 'security_managed_externally') IS NULL
                ALTER TABLE dbo.clients ADD security_managed_externally BIT NOT NULL CONSTRAINT DF_clients_sec_mgmt DEFAULT 0;
            IF COL_LENGTH('dbo.clients', 'security_managed_note') IS NULL
                ALTER TABLE dbo.clients ADD security_managed_note NVARCHAR(1000) NULL;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(bool Managed, string? Note)> GetSecurityManagementAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSecurityMgmtSchemaAsync(conn, null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT security_managed_externally, security_managed_note FROM dbo.clients WHERE client_id = @cid";
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (false, null);
        var managed = !r.IsDBNull(0) && r.GetBoolean(0);
        var note = r.IsDBNull(1) ? null : r.GetString(1);
        return (managed, note);
    }

    public async Task SetSecurityManagementAsync(int clientId, bool managed, string? note, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSecurityMgmtSchemaAsync(conn, null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.clients
            SET security_managed_externally = @managed, security_managed_note = @note
            WHERE client_id = @cid
            """;
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        cmd.Parameters.Add(new SqlParameter("@managed", managed));
        cmd.Parameters.Add(new SqlParameter("@note", (object?)trimmed ?? DBNull.Value)); // 8178: nunca null crudo
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
