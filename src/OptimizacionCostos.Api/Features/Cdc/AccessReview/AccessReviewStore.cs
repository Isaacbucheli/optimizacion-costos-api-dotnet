using System.Data;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

public interface IAccessReviewStore
{
    Task<int> CreateRunAsync(int clientId, string? requestedBy, CancellationToken ct = default);
    Task MarkRunningAsync(int runId, CancellationToken ct = default);
    Task MarkFinishedAsync(int runId, string status, string? error, CancellationToken ct = default);
    /// <summary>true si el cliente tiene una corrida queued|running.</summary>
    Task<bool> IsRunActiveAsync(int clientId, CancellationToken ct = default);
    Task<int> MarkOrphanedRunningAsFailedAsync(string error, CancellationToken ct = default);
    Task SaveResultsAsync(int runId,
        IReadOnlyList<AccessAssignmentRow> assignments, IReadOnlyList<AccessGuestRow> guests,
        IReadOnlyList<AccessGlobalAdminRow> globalAdmins, IReadOnlyList<AccessCredStatus> credStatuses,
        CancellationToken ct = default);
    Task<AccessRunRef?> GetLatestRunAsync(int clientId, CancellationToken ct = default);
    Task<IReadOnlyList<AccessRunRef>> ListRunsAsync(int clientId, int top = 20, CancellationToken ct = default);
    Task<AccessReviewSnapshot?> GetSnapshotAsync(int runId, CancellationToken ct = default);
}

/// <summary>Persistencia de corridas de revisión de accesos. Tablas schema-lazy (patrón power_history_job).</summary>
public sealed class SqlAccessReviewStore(ISqlConnectionFactory factory) : IAccessReviewStore
{
    private static bool _schemaEnsured;

    private static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        if (_schemaEnsured) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.cdc_access_review_run') IS NULL
            CREATE TABLE dbo.cdc_access_review_run (
                run_id INT IDENTITY PRIMARY KEY,
                client_id INT NOT NULL,
                status NVARCHAR(20) NOT NULL,
                started_at DATETIME2 NULL,
                finished_at DATETIME2 NULL,
                error NVARCHAR(2000) NULL,
                requested_by NVARCHAR(200) NULL,
                created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
            IF OBJECT_ID('dbo.cdc_access_review_cred_status') IS NULL
            CREATE TABLE dbo.cdc_access_review_cred_status (
                run_id INT NOT NULL,
                credential_id INT NOT NULL,
                credential_name NVARCHAR(200) NULL,
                arm_status NVARCHAR(20) NOT NULL,
                graph_status NVARCHAR(30) NOT NULL,
                detail NVARCHAR(1000) NULL,
                INDEX IX_cdc_arcs_run (run_id));
            IF OBJECT_ID('dbo.cdc_access_assignment') IS NULL
            CREATE TABLE dbo.cdc_access_assignment (
                assignment_row_id INT IDENTITY PRIMARY KEY,
                run_id INT NOT NULL,
                subscription_id NVARCHAR(50) NOT NULL,
                subscription_name NVARCHAR(200) NULL,
                subscription_state NVARCHAR(30) NULL,
                scope NVARCHAR(1000) NOT NULL,
                scope_level NVARCHAR(20) NOT NULL,
                role_name NVARCHAR(200) NOT NULL,
                role_definition_id NVARCHAR(400) NOT NULL,
                principal_object_id NVARCHAR(50) NOT NULL,
                principal_type NVARCHAR(30) NOT NULL,
                display_name NVARCHAR(300) NULL,
                login NVARCHAR(300) NULL,
                user_type NVARCHAR(10) NULL,
                via_group_id NVARCHAR(50) NULL,
                via_group_name NVARCHAR(300) NULL,
                account_enabled BIT NULL,
                last_sign_in DATETIME2 NULL,
                mfa_status NVARCHAR(20) NULL,
                INDEX IX_cdc_aa_run (run_id));
            IF OBJECT_ID('dbo.cdc_access_guest') IS NULL
            CREATE TABLE dbo.cdc_access_guest (
                guest_row_id INT IDENTITY PRIMARY KEY,
                run_id INT NOT NULL,
                object_id NVARCHAR(50) NOT NULL,
                display_name NVARCHAR(300) NULL,
                email NVARCHAR(300) NULL,
                external_domain NVARCHAR(200) NULL,
                account_enabled BIT NOT NULL,
                external_state NVARCHAR(30) NULL,
                created_at_azure DATETIME2 NULL,
                last_sign_in DATETIME2 NULL,
                roles_in_subs NVARCHAR(MAX) NULL,
                mfa_status NVARCHAR(20) NULL,
                INDEX IX_cdc_ag_run (run_id));
            IF OBJECT_ID('dbo.cdc_access_global_admin') IS NULL
            CREATE TABLE dbo.cdc_access_global_admin (
                ga_row_id INT IDENTITY PRIMARY KEY,
                run_id INT NOT NULL,
                object_id NVARCHAR(50) NOT NULL,
                display_name NVARCHAR(300) NULL,
                upn NVARCHAR(300) NULL,
                user_type NVARCHAR(20) NULL,
                account_enabled BIT NULL,
                last_sign_in DATETIME2 NULL,
                mfa_status NVARCHAR(20) NULL,
                INDEX IX_cdc_aga_run (run_id));
            -- Columnas agregadas después del despliegue inicial: el bloque de arriba solo crea
            -- tablas ausentes, así que las tablas ya existentes necesitan ALTER idempotente.
            IF COL_LENGTH('dbo.cdc_access_assignment','role_class') IS NULL
                ALTER TABLE dbo.cdc_access_assignment ADD role_class NVARCHAR(30) NULL;
            IF COL_LENGTH('dbo.cdc_access_assignment','is_custom_role') IS NULL
                ALTER TABLE dbo.cdc_access_assignment ADD is_custom_role BIT NULL;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _schemaEnsured = true;
    }

    private static object Db(object? v) => v ?? DBNull.Value;

    /// <summary>
    /// Inserta filas con SqlBulkCopy dentro de la transacción en curso. El mapeo es explícito por
    /// nombre de columna: la tabla tiene una columna IDENTITY que no se escribe, así que depender del
    /// orden ordinal desplazaría todos los valores una posición.
    /// </summary>
    private static async Task BulkInsertAsync<T>(
        SqlConnection conn, SqlTransaction tx, string table, IReadOnlyList<T> rows,
        (string Column, Type Type, Func<T, object> Value)[] columns, CancellationToken ct)
    {
        if (rows.Count == 0) return;

        using var data = new DataTable();
        foreach (var (column, type, _) in columns)
            data.Columns.Add(column, type).AllowDBNull = true;

        foreach (var row in rows)
        {
            var values = new object[columns.Length];
            for (var i = 0; i < columns.Length; i++) values[i] = columns[i].Value(row);
            data.Rows.Add(values);
        }

        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = table,
            BatchSize = 2000,
            BulkCopyTimeout = 300,
        };
        foreach (var (column, _, _) in columns) bulk.ColumnMappings.Add(column, column);
        await bulk.WriteToServerAsync(data, ct);
    }

    public async Task<int> CreateRunAsync(int clientId, string? requestedBy, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.cdc_access_review_run (client_id, status, requested_by)
            OUTPUT INSERTED.run_id VALUES (@cid, 'queued', @actor)
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        cmd.Parameters.Add(new SqlParameter("@actor", Db(requestedBy)));
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task MarkRunningAsync(int runId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dbo.cdc_access_review_run SET status='running', started_at=SYSUTCDATETIME() WHERE run_id=@id";
        cmd.Parameters.Add(new SqlParameter("@id", runId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFinishedAsync(int runId, string status, string? error, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dbo.cdc_access_review_run SET status=@st, finished_at=SYSUTCDATETIME(), error=@err WHERE run_id=@id";
        cmd.Parameters.Add(new SqlParameter("@st", status));
        cmd.Parameters.Add(new SqlParameter("@err", Db(error is { Length: > 2000 } ? error[..2000] : error)));
        cmd.Parameters.Add(new SqlParameter("@id", runId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsRunActiveAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM dbo.cdc_access_review_run WHERE client_id=@cid AND status IN ('queued','running')";
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        return (int)(await cmd.ExecuteScalarAsync(ct))! > 0;
    }

    public async Task<int> MarkOrphanedRunningAsFailedAsync(string error, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.cdc_access_review_run SET status='error', finished_at=SYSUTCDATETIME(), error=@err
            WHERE status IN ('queued','running')
            """;
        cmd.Parameters.Add(new SqlParameter("@err", error));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveResultsAsync(int runId,
        IReadOnlyList<AccessAssignmentRow> assignments, IReadOnlyList<AccessGuestRow> guests,
        IReadOnlyList<AccessGlobalAdminRow> globalAdmins, IReadOnlyList<AccessCredStatus> credStatuses,
        CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // Asignaciones y guests van por SqlBulkCopy: son los dos conjuntos grandes (en un tenant real,
        // 6013 y 3943 filas). Insertarlas de a un comando costaba ~106 ms por fila contra Azure SQL,
        // o sea 1060 s = el 72% de una corrida de 24 minutos. Las otras dos tablas son de decenas de
        // filas y no justifican el andamiaje.
        await BulkInsertAsync(conn, tx, "dbo.cdc_access_assignment", assignments,
            [
                ("run_id", typeof(int), _ => runId),
                ("subscription_id", typeof(string), a => a.SubscriptionId),
                ("subscription_name", typeof(string), a => Db(a.SubscriptionName)),
                ("subscription_state", typeof(string), a => Db(a.SubscriptionState)),
                ("scope", typeof(string), a => a.Scope),
                ("scope_level", typeof(string), a => a.ScopeLevel),
                ("role_name", typeof(string), a => a.RoleName),
                ("role_definition_id", typeof(string), a => a.RoleDefinitionId),
                ("principal_object_id", typeof(string), a => a.PrincipalObjectId),
                ("principal_type", typeof(string), a => a.PrincipalType),
                ("display_name", typeof(string), a => Db(a.DisplayName)),
                ("login", typeof(string), a => Db(a.Login)),
                ("user_type", typeof(string), a => Db(a.UserType)),
                ("via_group_id", typeof(string), a => Db(a.ViaGroupId)),
                ("via_group_name", typeof(string), a => Db(a.ViaGroupName)),
                ("account_enabled", typeof(bool), a => Db(a.AccountEnabled)),
                ("last_sign_in", typeof(DateTime), a => Db(a.LastSignIn?.UtcDateTime)),
                ("mfa_status", typeof(string), a => Db(a.MfaStatus)),
                ("role_class", typeof(string), a => Db(a.RoleClass)),
                ("is_custom_role", typeof(bool), a => a.IsCustomRole),
            ], ct);

        await BulkInsertAsync(conn, tx, "dbo.cdc_access_guest", guests,
            [
                ("run_id", typeof(int), _ => runId),
                ("object_id", typeof(string), g => g.ObjectId),
                ("display_name", typeof(string), g => Db(g.DisplayName)),
                ("email", typeof(string), g => Db(g.Email)),
                ("external_domain", typeof(string), g => Db(g.ExternalDomain)),
                ("account_enabled", typeof(bool), g => g.AccountEnabled),
                ("external_state", typeof(string), g => Db(g.ExternalState)),
                ("created_at_azure", typeof(DateTime), g => Db(g.CreatedAtAzure?.UtcDateTime)),
                ("last_sign_in", typeof(DateTime), g => Db(g.LastSignIn?.UtcDateTime)),
                ("roles_in_subs", typeof(string), g => Db(g.RolesInSubs)),
                ("mfa_status", typeof(string), g => Db(g.MfaStatus)),
            ], ct);
        foreach (var ga in globalAdmins)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO dbo.cdc_access_global_admin (run_id, object_id, display_name, upn, user_type,
                    account_enabled, last_sign_in, mfa_status)
                VALUES (@run, @oid, @dname, @upn, @utype, @enabled, @lsi, @mfa)
                """;
            cmd.Parameters.AddRange([
                new("@run", runId), new("@oid", ga.ObjectId), new("@dname", Db(ga.DisplayName)),
                new("@upn", Db(ga.Upn)), new("@utype", Db(ga.UserType)), new("@enabled", Db(ga.AccountEnabled)),
                new("@lsi", Db(ga.LastSignIn?.UtcDateTime)), new("@mfa", Db(ga.MfaStatus))]);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var c in credStatuses)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO dbo.cdc_access_review_cred_status (run_id, credential_id, credential_name, arm_status, graph_status, detail)
                VALUES (@run, @cid, @cname, @arm, @graph, @detail)
                """;
            cmd.Parameters.AddRange([
                new("@run", runId), new("@cid", c.CredentialId), new("@cname", Db(c.CredentialName)),
                new("@arm", c.ArmStatus), new("@graph", c.GraphStatus), new("@detail", Db(c.Detail))]);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static AccessRunRef ReadRun(SqlDataReader r) => new(
        r.GetInt32(0), r.GetInt32(1), r.GetString(2),
        r.IsDBNull(3) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc)),
        r.IsDBNull(4) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc)),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6));

    private const string RunCols = "run_id, client_id, status, started_at, finished_at, error, requested_by";

    public async Task<AccessRunRef?> GetLatestRunAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT TOP 1 {RunCols} FROM dbo.cdc_access_review_run WHERE client_id=@cid ORDER BY run_id DESC";
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRun(r) : null;
    }

    public async Task<IReadOnlyList<AccessRunRef>> ListRunsAsync(int clientId, int top = 20, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT TOP (@top) {RunCols} FROM dbo.cdc_access_review_run WHERE client_id=@cid ORDER BY run_id DESC";
        cmd.Parameters.Add(new SqlParameter("@top", top));
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var list = new List<AccessRunRef>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadRun(r));
        return list;
    }

    public async Task<AccessReviewSnapshot?> GetSnapshotAsync(int runId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        AccessRunRef? run = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {RunCols} FROM dbo.cdc_access_review_run WHERE run_id=@id";
            cmd.Parameters.Add(new SqlParameter("@id", runId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) run = ReadRun(r);
        }
        if (run is null) return null;

        static DateTimeOffset? Dt(SqlDataReader r, int i) =>
            r.IsDBNull(i) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc));
        static string? S(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static bool? B(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetBoolean(i);

        var creds = new List<AccessCredStatus>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT credential_id, credential_name, arm_status, graph_status, detail FROM dbo.cdc_access_review_cred_status WHERE run_id=@id";
            cmd.Parameters.Add(new SqlParameter("@id", runId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                creds.Add(new(r.GetInt32(0), S(r, 1), r.GetString(2), r.GetString(3), S(r, 4)));
        }

        var assignments = new List<AccessAssignmentRow>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT subscription_id, subscription_name, subscription_state, scope, scope_level, role_name,
                       role_definition_id, principal_object_id, principal_type, display_name, login, user_type,
                       via_group_id, via_group_name, account_enabled, last_sign_in, mfa_status,
                       role_class, is_custom_role
                FROM dbo.cdc_access_assignment WHERE run_id=@id
                ORDER BY subscription_name, display_name, role_name
                """;
            cmd.Parameters.Add(new SqlParameter("@id", runId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                // Corridas anteriores a la clasificación traen role_class NULL: "sin clasificar".
                assignments.Add(new(r.GetString(0), S(r, 1), S(r, 2), r.GetString(3), r.GetString(4), r.GetString(5),
                    r.GetString(6), r.GetString(7), r.GetString(8), S(r, 9), S(r, 10), S(r, 11),
                    S(r, 12), S(r, 13), B(r, 14), Dt(r, 15), S(r, 16),
                    S(r, 17), B(r, 18) ?? false));
        }

        var guests = new List<AccessGuestRow>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT object_id, display_name, email, external_domain, account_enabled, external_state,
                       created_at_azure, last_sign_in, roles_in_subs, mfa_status
                FROM dbo.cdc_access_guest WHERE run_id=@id ORDER BY display_name
                """;
            cmd.Parameters.Add(new SqlParameter("@id", runId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                guests.Add(new(r.GetString(0), S(r, 1), S(r, 2), S(r, 3), r.GetBoolean(4), S(r, 5),
                    Dt(r, 6), Dt(r, 7), S(r, 8), S(r, 9)));
        }

        var gas = new List<AccessGlobalAdminRow>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT object_id, display_name, upn, user_type, account_enabled, last_sign_in, mfa_status
                FROM dbo.cdc_access_global_admin WHERE run_id=@id ORDER BY display_name
                """;
            cmd.Parameters.Add(new SqlParameter("@id", runId));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                gas.Add(new(r.GetString(0), S(r, 1), S(r, 2), S(r, 3), B(r, 4), Dt(r, 5), S(r, 6)));
        }

        return new AccessReviewSnapshot(run, creds, assignments, guests, gas);
    }
}
