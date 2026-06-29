using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Índice de informes mensuales. Port de app/reports/schema.py. El JSON completo del informe vive
/// en Blob (container outputs); en SQL solo queda una fila liviana por cliente+mes (decisión
/// explícita para no escalar la BD) + el plan de acción editable.
/// </summary>
public static class ReportsSchema
{
    public static async Task EnsureSchemaAsync(ISqlConnectionFactory factory, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
    }

    public static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.client_monthly_report', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.client_monthly_report (
                    report_id INT IDENTITY(1,1) PRIMARY KEY,
                    client_id INT NOT NULL,
                    period_year INT NOT NULL,
                    period_month INT NOT NULL,
                    analysis_id INT NULL,
                    blob_container NVARCHAR(100) NOT NULL,
                    blob_name NVARCHAR(400) NOT NULL,
                    status NVARCHAR(40) NOT NULL CONSTRAINT DF_cmr_status DEFAULT 'completed',
                    is_partial BIT NOT NULL CONSTRAINT DF_cmr_partial DEFAULT 0,
                    summary_json NVARCHAR(MAX) NULL,
                    generated_by NVARCHAR(200) NULL,
                    generated_at DATETIME2 NOT NULL CONSTRAINT DF_cmr_generated DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT UQ_client_monthly_report UNIQUE (client_id, period_year, period_month),
                    CONSTRAINT FK_client_monthly_report_client FOREIGN KEY (client_id) REFERENCES dbo.clients (client_id)
                );
            END
            IF OBJECT_ID('dbo.report_action_plan', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.report_action_plan (
                    item_id INT IDENTITY(1,1) PRIMARY KEY,
                    client_id INT NOT NULL,
                    period_year INT NOT NULL,
                    period_month INT NOT NULL,
                    orden INT NOT NULL CONSTRAINT DF_rap_orden DEFAULT 0,
                    prioridad NVARCHAR(10) NOT NULL,
                    hallazgo NVARCHAR(500) NOT NULL,
                    accion NVARCHAR(500) NULL,
                    responsable NVARCHAR(120) NULL,
                    estado NVARCHAR(30) NOT NULL CONSTRAINT DF_rap_estado DEFAULT 'Pendiente',
                    updated_by NVARCHAR(200) NULL,
                    updated_at DATETIME2 NOT NULL CONSTRAINT DF_rap_updated DEFAULT SYSUTCDATETIME(),
                    CONSTRAINT FK_report_action_plan_client FOREIGN KEY (client_id) REFERENCES dbo.clients (client_id)
                );
                CREATE INDEX IX_report_action_plan_period ON dbo.report_action_plan (client_id, period_year, period_month);
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
