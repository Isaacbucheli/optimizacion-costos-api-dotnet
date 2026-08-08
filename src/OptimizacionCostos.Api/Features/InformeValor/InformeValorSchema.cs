using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>
/// Esquema del módulo Informe de valor (idempotente, estilo ConsultantsSchema). Sin seed.
/// El índice único va sobre natural_key_hash y no sobre las columnas de la clave: sumadas
/// en NVARCHAR llegan a 3334 bytes, muy por encima del límite de 1700 de una clave de
/// índice no agrupada (el CREATE pasa con advertencia y revienta después con el error 1946).
/// </summary>
public static class InformeValorSchema
{
    public static readonly IReadOnlyList<string> Statements =
    [
        """
        IF OBJECT_ID('dbo.informe_valor_ingesta', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.informe_valor_ingesta (
                ingesta_id INT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL CONSTRAINT FK_iv_ingesta_client REFERENCES dbo.clients(client_id),
                kind NVARCHAR(20) NOT NULL,
                source_file_name NVARCHAR(400) NOT NULL,
                rows_total INT NOT NULL CONSTRAINT DF_iv_ingesta_total DEFAULT 0,
                rows_processed INT NOT NULL CONSTRAINT DF_iv_ingesta_proc DEFAULT 0,
                rows_skipped INT NOT NULL CONSTRAINT DF_iv_ingesta_skip DEFAULT 0,
                rows_merged INT NOT NULL CONSTRAINT DF_iv_ingesta_merged DEFAULT 0,
                truncated_values INT NOT NULL CONSTRAINT DF_iv_ingesta_trunc DEFAULT 0,
                status NVARCHAR(30) NOT NULL,
                error_message NVARCHAR(1000) NULL,
                warnings_json NVARCHAR(4000) NULL,
                started_at DATETIME2 NOT NULL,
                completed_at DATETIME2 NULL,
                created_by NVARCHAR(200) NULL
            );
            CREATE INDEX IX_iv_ingesta_client ON dbo.informe_valor_ingesta (client_id, kind, started_at DESC);
        END
        """,
        """
        IF OBJECT_ID('dbo.informe_valor_facturacion', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.informe_valor_facturacion (
                row_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL CONSTRAINT FK_iv_fact_client REFERENCES dbo.clients(client_id),
                ingesta_id INT NOT NULL,
                natural_key_hash CHAR(64) NOT NULL,
                tenant NVARCHAR(100) NULL,
                subscription_name NVARCHAR(200) NULL,
                subscription_id NVARCHAR(100) NULL,
                resource_group NVARCHAR(255) NULL,
                resource_name NVARCHAR(512) NULL,
                cost_center NVARCHAR(200) NULL,
                category NVARCHAR(200) NULL,
                subcategory NVARCHAR(200) NULL,
                service NVARCHAR(200) NULL,
                quantity DECIMAL(28,10) NULL,
                unit NVARCHAR(100) NULL,
                rate DECIMAL(28,10) NULL,
                pvp DECIMAL(28,10) NOT NULL,
                period_year SMALLINT NOT NULL,
                period_month TINYINT NOT NULL
            );
            CREATE UNIQUE INDEX UX_iv_fact_key ON dbo.informe_valor_facturacion (client_id, natural_key_hash);
            CREATE INDEX IX_iv_fact_periodo ON dbo.informe_valor_facturacion (client_id, period_year, period_month);
        END
        """,
        """
        IF OBJECT_ID('dbo.informe_valor_caso', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.informe_valor_caso (
                row_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL CONSTRAINT FK_iv_caso_client REFERENCES dbo.clients(client_id),
                ingesta_id INT NOT NULL,
                natural_key_hash CHAR(64) NOT NULL,
                caso NVARCHAR(120) NULL,
                fecha_registro DATE NULL,
                estado NVARCHAR(120) NULL,
                sla_horas DECIMAL(18,4) NULL,
                duracion_cruda DECIMAL(18,4) NULL,
                cumple NVARCHAR(20) NULL,
                categoria NVARCHAR(200) NULL,
                subcategoria NVARCHAR(300) NULL,
                horario NVARCHAR(120) NULL
            );
            CREATE UNIQUE INDEX UX_iv_caso_key ON dbo.informe_valor_caso (client_id, natural_key_hash);
        END
        """,
        """
        IF OBJECT_ID('dbo.informe_valor_rbac', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.informe_valor_rbac (
                row_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL CONSTRAINT FK_iv_rbac_client REFERENCES dbo.clients(client_id),
                ingesta_id INT NOT NULL,
                natural_key_hash CHAR(64) NOT NULL,
                sheet_name NVARCHAR(200) NULL,
                suscripcion NVARCHAR(200) NULL,
                scope NVARCHAR(900) NULL,
                nivel NVARCHAR(60) NULL,
                rol NVARCHAR(200) NULL,
                tipo NVARCHAR(60) NULL,
                nombre NVARCHAR(300) NULL,
                login NVARCHAR(300) NULL,
                cuenta_activa NVARCHAR(30) NULL,
                ultimo_login NVARCHAR(60) NULL
            );
            CREATE UNIQUE INDEX UX_iv_rbac_key ON dbo.informe_valor_rbac (client_id, natural_key_hash);
        END
        """,
        // soft-migration ingesta (tablas preexistentes de la entrega 1, ya en PR): la calculadora
        // publica "revisado línea por línea sobre N registros" y en la plantilla ese N son las
        // filas aceptadas ANTES de fusionar (ver BitcostParser.ParseResult.RowsMerged). Sin esta
        // columna la cifra sale por debajo del histórico y no hay forma de reproducirla desde la
        // base para una carga ya hecha antes de este fix.
        """
        IF COL_LENGTH('dbo.informe_valor_ingesta', 'rows_merged') IS NULL
            ALTER TABLE dbo.informe_valor_ingesta ADD rows_merged INT NOT NULL CONSTRAINT DF_iv_ingesta_merged DEFAULT 0;
        """,
    ];

    public static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        foreach (var sql in Statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
