using System.Data;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Cdc.AccessReview;

public interface IAccessReviewDecisionStore
{
    /// <summary>Decisiones del cliente, por access_key. Sin run_id: la decisión vive por cliente y eso
    /// es lo que hace que sobreviva a la re-sincronización.</summary>
    Task<IReadOnlyDictionary<string, AccessDecision>> GetForClientAsync(int clientId, CancellationToken ct = default);

    /// <summary>Upsert por lote de decisiones sobre accesos. Devuelve cuántas filas se escribieron.</summary>
    Task<int> UpsertAsync(int clientId, IReadOnlyList<AccessDecisionInput> inputs, string? actor,
        int? runId, CancellationToken ct = default);

    /// <summary>Acepta un hallazgo de umbral (los que no tienen accesos individuales que marcar).</summary>
    Task AcceptFindingAsync(int clientId, string findingKey, string note, string? actor, int? runId,
        CancellationToken ct = default);
}

public sealed class SqlAccessReviewDecisionStore(ISqlConnectionFactory factory) : IAccessReviewDecisionStore
{
    private static bool _schemaEnsured;

    private static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        if (_schemaEnsured) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.cdc_access_decision') IS NULL
            CREATE TABLE dbo.cdc_access_decision (
                decision_id INT IDENTITY PRIMARY KEY,
                client_id INT NOT NULL,
                access_key CHAR(64) NOT NULL,
                principal_object_id NVARCHAR(50) NOT NULL,
                role_key NVARCHAR(100) NOT NULL,
                scope NVARCHAR(1000) NOT NULL,
                finding_key NVARCHAR(60) NULL,
                decision NVARCHAR(20) NOT NULL,
                note NVARCHAR(1000) NULL,
                decided_by NVARCHAR(200) NULL,
                decided_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                decided_run_id INT NULL,
                CONSTRAINT UQ_cdc_access_decision UNIQUE (client_id, access_key));
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _schemaEnsured = true;
    }

    private static object Db(object? v) => v ?? DBNull.Value;

    public async Task<IReadOnlyDictionary<string, AccessDecision>> GetForClientAsync(
        int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        // runs_since = corridas del cliente posteriores a la que se decidió. Permite afirmar "hace N
        // corridas" sin guardar historial por corrida.
        cmd.CommandText = """
            SELECT d.access_key, d.principal_object_id, d.role_key, d.scope, d.finding_key,
                   d.decision, d.note, d.decided_by, d.decided_at, d.decided_run_id,
                   (SELECT COUNT(*) FROM dbo.cdc_access_review_run r
                     WHERE r.client_id = d.client_id AND r.status IN ('ok','partial')
                       AND r.run_id > ISNULL(d.decided_run_id, 2147483647)) AS runs_since
            FROM dbo.cdc_access_decision d
            WHERE d.client_id = @cid
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));

        var map = new Dictionary<string, AccessDecision>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            static string? S(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
            var key = r.GetString(0);
            map[key] = new AccessDecision(
                key, r.GetString(1), r.GetString(2), r.GetString(3), S(r, 4),
                r.GetString(5), S(r, 6), S(r, 7),
                new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc)),
                r.IsDBNull(9) ? null : r.GetInt32(9),
                r.GetInt32(10));
        }
        return map;
    }

    public async Task<int> UpsertAsync(int clientId, IReadOnlyList<AccessDecisionInput> inputs,
        string? actor, int? runId, CancellationToken ct = default)
    {
        if (inputs.Count == 0) return 0;

        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // Dedup por clave: dos filas de la tabla pueden ser el MISMO acceso (una directa y otra
        // derivada de un grupo). Sin esto el conteo devuelto diría "2 decisiones" para un solo
        // acceso, que es lo que el usuario ve en el toast.
        var porClave = new Dictionary<string, (string Principal, string RoleKey, string Scope, AccessDecisionInput Input)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            var key = AccessReviewAccessKey.For(input.PrincipalObjectId, input.RoleDefinitionId ?? "", input.Scope);
            porClave[key] = (input.PrincipalObjectId,
                AccessReviewRoleClassifier.RoleKey(input.RoleDefinitionId ?? ""), input.Scope, input);
        }

        var saved = 0;
        foreach (var (accessKey, v) in porClave)
        {
            saved += await WriteAsync(conn, tx, clientId, accessKey,
                v.Principal, v.RoleKey, v.Scope, null,
                v.Input.Decision, v.Input.Note, actor, runId, ct);
        }
        await tx.CommitAsync(ct);
        return saved;
    }

    public async Task AcceptFindingAsync(int clientId, string findingKey, string note, string? actor,
        int? runId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await WriteAsync(conn, tx, clientId, AccessReviewAccessKey.ForFinding(findingKey),
            "", "", "", findingKey, AccessDecisionValues.Justificado, note, actor, runId, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Upsert de una fila. El índice único (client_id, access_key) hace de clave natural:
    /// volver a decidir sobre el mismo acceso sobrescribe, no acumula.</summary>
    private static async Task<int> WriteAsync(
        SqlConnection conn, SqlTransaction tx, int clientId, string accessKey,
        string principal, string roleKey, string scope, string? findingKey,
        string decision, string? note, string? actor, int? runId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE dbo.cdc_access_decision
               SET decision = @dec, note = @note, decided_by = @actor,
                   decided_at = SYSUTCDATETIME(), decided_run_id = @run
             WHERE client_id = @cid AND access_key = @key;
            IF @@ROWCOUNT = 0
            INSERT INTO dbo.cdc_access_decision
                (client_id, access_key, principal_object_id, role_key, scope, finding_key,
                 decision, note, decided_by, decided_run_id)
            VALUES (@cid, @key, @pid, @rkey, @scope, @fkey, @dec, @note, @actor, @run);
            """;
        cmd.Parameters.AddRange([
            new("@cid", clientId), new("@key", accessKey), new("@pid", principal),
            new("@rkey", roleKey), new("@scope", scope), new("@fkey", Db(findingKey)),
            new("@dec", decision), new("@note", Db(note)), new("@actor", Db(actor)),
            new("@run", Db(runId))]);
        await cmd.ExecuteNonQueryAsync(ct);
        return 1;
    }
}
