using System.Security.Claims;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.CostEngine.Api;

/// <summary>
/// Implementacion SQL del control de acceso. Port de app/access_control.py.
/// El rol que autoriza es el claim ClaimTypes.Role (rol vivo de BD, inyectado por
/// AuthSetup). El email es el claim "sub". SQL siempre parametrizado.
/// </summary>
public sealed class SqlAnalysisAccess(ISqlConnectionFactory factory) : IAnalysisAccess
{
    // Roles con acceso a todos los clientes (no requieren asignacion). Paridad con
    // GLOBAL_ACCESS_ROLES = {"admin"} del Python.
    private static bool HasGlobalAccess(ClaimsPrincipal user) => user.IsInRole(Roles.Admin);

    public async Task<IReadOnlySet<int>?> AccessibleClientIdsAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (HasGlobalAccess(user))
            return null; // None == "todos" (admin).

        await using var conn = await factory.OpenAsync(ct);
        await EnsureAssignmentSchemaAsync(conn, ct);

        var email = user.FindFirst("sub")?.Value;
        var userId = await UserIdForEmailAsync(conn, email, ct);
        if (userId is null)
            return new HashSet<int>(); // sin usuario -> sin acceso.

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_id FROM dbo.user_client_assignment WHERE user_id = @user_id";
        cmd.Parameters.Add(new SqlParameter("@user_id", userId.Value));

        var ids = new HashSet<int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    public async Task<AccessCheck> AssertAnalysisAccessAsync(ClaimsPrincipal user, int analysisId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var clientId = await ClientIdForAnalysisAsync(conn, analysisId, ct);
        if (clientId is null)
            return AccessCheck.NotFound("Analysis not found");

        return await AssertClientAccessAsync(conn, user, clientId.Value, ct);
    }

    public async Task<AccessCheck> AssertCostResultAccessAsync(ClaimsPrincipal user, int costResultId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT analysis_id FROM dbo.cost_results WHERE cost_result_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", costResultId));
        var analysisIdObj = await cmd.ExecuteScalarAsync(ct);
        if (analysisIdObj is null or DBNull)
            return AccessCheck.NotFound("cost_result not found");

        var analysisId = Convert.ToInt32(analysisIdObj);
        var clientId = await ClientIdForAnalysisAsync(conn, analysisId, ct);
        if (clientId is null)
            return AccessCheck.NotFound("Analysis not found");

        return await AssertClientAccessAsync(conn, user, clientId.Value, ct);
    }

    // -------------------- internos --------------------

    /// <summary>Lanza/devuelve 403 si el usuario no puede acceder al cliente indicado.</summary>
    private async Task<AccessCheck> AssertClientAccessAsync(
        SqlConnection conn, ClaimsPrincipal user, int clientId, CancellationToken ct)
    {
        if (HasGlobalAccess(user))
            return AccessCheck.Allow();

        await EnsureAssignmentSchemaAsync(conn, ct);
        var email = user.FindFirst("sub")?.Value;
        var userId = await UserIdForEmailAsync(conn, email, ct);
        if (userId is null)
            return AccessCheck.Forbidden();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM dbo.user_client_assignment WHERE user_id = @user_id AND client_id = @client_id";
        cmd.Parameters.Add(new SqlParameter("@user_id", userId.Value));
        cmd.Parameters.Add(new SqlParameter("@client_id", clientId));
        var hit = await cmd.ExecuteScalarAsync(ct);
        return hit is null or DBNull ? AccessCheck.Forbidden() : AccessCheck.Allow();
    }

    private static async Task<int?> ClientIdForAnalysisAsync(SqlConnection conn, int analysisId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_id FROM dbo.cost_analysis WHERE analysis_id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", analysisId));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    private static async Task<int?> UserIdForEmailAsync(SqlConnection conn, string? email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id FROM dbo.app_users WHERE email = @email";
        cmd.Parameters.Add(new SqlParameter("@email", email));
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }

    /// <summary>Crea la tabla de asignacion si no existe (idempotente). Port de ensure_assignment_schema.</summary>
    private static async Task EnsureAssignmentSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.user_client_assignment', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.user_client_assignment (
                    user_id INT NOT NULL,
                    client_id INT NOT NULL,
                    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT PK_user_client_assignment PRIMARY KEY (user_id, client_id),
                    CONSTRAINT FK_uca_user FOREIGN KEY (user_id)
                        REFERENCES dbo.app_users (user_id) ON DELETE CASCADE,
                    CONSTRAINT FK_uca_client FOREIGN KEY (client_id)
                        REFERENCES dbo.clients (client_id) ON DELETE CASCADE
                );
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
