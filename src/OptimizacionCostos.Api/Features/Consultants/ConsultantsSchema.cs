using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.Consultants;

/// <summary>
/// Crea las 3 tablas del módulo (idempotente, estilo PolicyCatalogSchema). SIN seed:
/// los datos reales (clientes y consultores) son sensibles y los repos son públicos;
/// el import inicial se hace fuera del repo vía API con sesión admin.
/// </summary>
public static class ConsultantsSchema
{
    public static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await ExecAsync(conn, ct, """
            IF OBJECT_ID('dbo.cdc_people', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.cdc_people (
                    person_id INT IDENTITY(1,1) PRIMARY KEY,
                    name NVARCHAR(200) NOT NULL,
                    email NVARCHAR(300) NULL,
                    person_type NVARCHAR(20) NOT NULL,
                    is_active BIT NOT NULL CONSTRAINT DF_cdc_people_active DEFAULT 1,
                    created_at DATETIME2 NOT NULL CONSTRAINT DF_cdc_people_created DEFAULT SYSUTCDATETIME(),
                    updated_at DATETIME2 NULL
                );
            END
            """);

        await ExecAsync(conn, ct, """
            IF OBJECT_ID('dbo.cdc_assignments', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.cdc_assignments (
                    assignment_id INT IDENTITY(1,1) PRIMARY KEY,
                    client_name NVARCHAR(200) NOT NULL,
                    service NVARCHAR(120) NULL,
                    category NVARCHAR(20) NULL,
                    databases NVARCHAR(300) NULL,
                    country NVARCHAR(80) NULL,
                    status NVARCHAR(40) NULL CONSTRAINT DF_cdc_assignments_status DEFAULT 'ACTIVO',
                    comercial_id INT NULL CONSTRAINT FK_cdc_assignments_comercial
                        REFERENCES dbo.cdc_people(person_id),
                    coordinator_id INT NULL CONSTRAINT FK_cdc_assignments_coordinator
                        REFERENCES dbo.cdc_people(person_id),
                    access_accounts NVARCHAR(MAX) NULL,
                    account_role NVARCHAR(300) NULL,
                    lighthouse NVARCHAR(300) NULL,
                    client_contact_name NVARCHAR(MAX) NULL,
                    client_contact_phone NVARCHAR(MAX) NULL,
                    client_contact_email NVARCHAR(MAX) NULL,
                    contract_end DATE NULL,
                    observations NVARCHAR(MAX) NULL,
                    is_active BIT NOT NULL CONSTRAINT DF_cdc_assignments_active DEFAULT 1,
                    created_at DATETIME2 NOT NULL CONSTRAINT DF_cdc_assignments_created DEFAULT SYSUTCDATETIME(),
                    updated_at DATETIME2 NULL
                );
            END
            """);

        await ExecAsync(conn, ct, """
            IF OBJECT_ID('dbo.cdc_assignment_consultants', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.cdc_assignment_consultants (
                    assignment_id INT NOT NULL CONSTRAINT FK_cdc_ac_assignment
                        REFERENCES dbo.cdc_assignments(assignment_id),
                    person_id INT NOT NULL CONSTRAINT FK_cdc_ac_person
                        REFERENCES dbo.cdc_people(person_id),
                    role NVARCHAR(10) NOT NULL,
                    CONSTRAINT PK_cdc_assignment_consultants PRIMARY KEY (assignment_id, person_id, role)
                );
            END
            """);
    }

    /// <summary>Convierte un JsonElement del body a un valor apto para SqlParameter.</summary>
    internal static object? JsonToDb(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt32(out var n) ? n : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => el.GetRawText(),
    };

    private static async Task ExecAsync(SqlConnection conn, CancellationToken ct, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
