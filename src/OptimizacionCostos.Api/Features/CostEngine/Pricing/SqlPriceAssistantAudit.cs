using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Persiste la auditoría del asistente en dbo.price_assistant_audit (crea la tabla si falta).
/// Port de _write_audit + ensure_schema (Python). Best-effort: si falla, NO rompe el cálculo.
/// </summary>
public sealed class SqlPriceAssistantAudit(ISqlConnectionFactory factory) : IPriceAssistantAudit
{
    public void Record(PriceAssistantAuditEntry e)
    {
        try
        {
            using var conn = factory.OpenAsync().GetAwaiter().GetResult();
            EnsureSchema(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dbo.price_assistant_audit
                  (service_key, component, request_context, candidate_count, selected_price_id,
                   selected_index, confidence, accepted, reason_es, raw_response)
                VALUES (@sk, @comp, @ctx, @cc, @spid, @sidx, @conf, @acc, @reason, @raw)
                """;
            cmd.Parameters.Add(new SqlParameter("@sk", e.ServiceKey));
            cmd.Parameters.Add(new SqlParameter("@comp", e.Component));
            cmd.Parameters.Add(new SqlParameter("@ctx", (object?)Trunc(e.ContextJson) ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@cc", e.CandidateCount));
            cmd.Parameters.Add(new SqlParameter("@spid", (object?)(e.SelectedPriceId is long pid ? (int)pid : (int?)null) ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@sidx", (object?)e.SelectedIndex ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@conf", (object?)e.Confidence ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@acc", e.Accepted ? 1 : 0));
            cmd.Parameters.Add(new SqlParameter("@reason", (object?)Trunc(e.ReasonEs) ?? DBNull.Value));
            cmd.Parameters.Add(new SqlParameter("@raw", (object?)Trunc(e.RawResponse) ?? DBNull.Value));
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Auditoría best-effort: nunca interrumpir el cálculo por un fallo de auditoría.
        }
    }

    private static string? Trunc(string? s) => s is null ? null : (s.Length > 4000 ? s[..4000] : s);

    private static void EnsureSchema(SqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.price_assistant_audit', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.price_assistant_audit (
                    audit_id INT IDENTITY(1,1) PRIMARY KEY,
                    service_key NVARCHAR(100) NOT NULL,
                    component NVARCHAR(100) NOT NULL,
                    request_context NVARCHAR(MAX) NULL,
                    candidate_count INT NOT NULL DEFAULT 0,
                    selected_price_id INT NULL,
                    selected_index INT NULL,
                    confidence FLOAT NULL,
                    accepted BIT NOT NULL DEFAULT 0,
                    reason_es NVARCHAR(MAX) NULL,
                    raw_response NVARCHAR(MAX) NULL,
                    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                );
            END
            """;
        cmd.ExecuteNonQuery();
    }
}
