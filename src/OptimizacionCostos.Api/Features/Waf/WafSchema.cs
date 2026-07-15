using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// DDL idempotente único del módulo WAF (port consolidado de ensure_waf_schema de schema.py).
/// Cada store llama EnsureSchemaAsync al inicio (schema lazy, no en Program.cs). Orden por FKs.
/// Resuelve el drift de tamaños entre specs (matrix_code 80, finding/comment/history BIGINT).
/// </summary>
public static class WafSchema
{
    public static async Task EnsureWafSchemaAsync(ISqlConnectionFactory factory, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureWafSchemaAsync(conn, ct);
    }

    public static async Task EnsureWafSchemaAsync(SqlConnection conn, CancellationToken ct = default)
    {
        foreach (var ddl in Statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static readonly string[] Statements =
    [
        // 1. canonical
        """
        IF OBJECT_ID('dbo.waf_recommendation_canonical', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.waf_recommendation_canonical (
                canonical_id INT IDENTITY(1,1) PRIMARY KEY,
                advisor_name NVARCHAR(500) NOT NULL,
                advisor_category NVARCHAR(100) NOT NULL,
                pillar_number TINYINT NOT NULL,
                review_scope_es NVARCHAR(1000) NOT NULL,
                benefit_es NVARCHAR(2000) NOT NULL,
                client_action_es NVARCHAR(2000) NOT NULL,
                bit_action_es NVARCHAR(2000) NOT NULL,
                is_excluded BIT NOT NULL CONSTRAINT DF_waf_canonical_excluded DEFAULT 0,
                exclusion_reason NVARCHAR(1000) NULL,
                consolidates_to_id INT NULL,
                ai_review_status NVARCHAR(30) NULL,
                ai_decision NVARCHAR(30) NULL,
                ai_confidence DECIMAL(5,4) NULL,
                ai_possible_additional_cost BIT NULL,
                ai_cost_reason NVARCHAR(1000) NULL,
                ai_exclusion_reason NVARCHAR(1000) NULL,
                ai_duplicate_group_key NVARCHAR(200) NULL,
                ai_raw_model_text NVARCHAR(4000) NULL,
                ai_reviewed_by NVARCHAR(255) NULL,
                ai_reviewed_at DATETIME2 NULL,
                created_at DATETIME2 NOT NULL CONSTRAINT DF_waf_canonical_created DEFAULT SYSUTCDATETIME(),
                updated_at DATETIME2 NOT NULL CONSTRAINT DF_waf_canonical_updated DEFAULT SYSUTCDATETIME(),
                CONSTRAINT CK_waf_canonical_pillar CHECK (pillar_number BETWEEN 1 AND 5),
                CONSTRAINT FK_waf_canonical_consolidates FOREIGN KEY (consolidates_to_id)
                    REFERENCES dbo.waf_recommendation_canonical(canonical_id)
            );
            CREATE UNIQUE INDEX UX_waf_canonical_advisor ON dbo.waf_recommendation_canonical(advisor_name, advisor_category);
        END
        """,
        // soft-migrations canonical (tablas preexistentes)
        """
        IF COL_LENGTH('dbo.waf_recommendation_canonical', 'ai_duplicate_group_key') IS NULL ALTER TABLE dbo.waf_recommendation_canonical ADD ai_duplicate_group_key NVARCHAR(200) NULL;
        IF COL_LENGTH('dbo.waf_recommendation_canonical', 'ai_raw_model_text') IS NULL ALTER TABLE dbo.waf_recommendation_canonical ADD ai_raw_model_text NVARCHAR(4000) NULL;
        IF COL_LENGTH('dbo.waf_recommendation_canonical', 'ai_reviewed_by') IS NULL ALTER TABLE dbo.waf_recommendation_canonical ADD ai_reviewed_by NVARCHAR(255) NULL;
        """,
        // 2. recommendation
        """
        IF OBJECT_ID('dbo.waf_recommendation', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.waf_recommendation (
                recommendation_id INT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL,
                canonical_id INT NOT NULL,
                matrix_code NVARCHAR(80) NOT NULL,
                business_impact NVARCHAR(30) NOT NULL,
                impact_number TINYINT NOT NULL,
                resource_count INT NOT NULL CONSTRAINT DF_waf_rec_rescount DEFAULT 0,
                first_seen_at DATETIME2 NOT NULL,
                last_seen_at DATETIME2 NOT NULL,
                is_active BIT NOT NULL CONSTRAINT DF_waf_rec_active DEFAULT 1,
                is_dismissed BIT NOT NULL CONSTRAINT DF_waf_rec_dismissed DEFAULT 0,
                dismissed_at DATETIME2 NULL,
                dismissed_by NVARCHAR(255) NULL,
                dismissal_reason NVARCHAR(1000) NULL,
                CONSTRAINT UQ_waf_rec_client_canonical UNIQUE (client_id, canonical_id),
                CONSTRAINT FK_waf_rec_client FOREIGN KEY (client_id) REFERENCES dbo.clients(client_id),
                CONSTRAINT FK_waf_rec_canonical FOREIGN KEY (canonical_id) REFERENCES dbo.waf_recommendation_canonical(canonical_id)
            );
            CREATE INDEX IX_waf_rec_client_active ON dbo.waf_recommendation(client_id, is_active, is_dismissed);
        END
        """,
        // 3. resource_finding
        """
        IF OBJECT_ID('dbo.waf_resource_finding', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.waf_resource_finding (
                finding_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                recommendation_id INT NOT NULL,
                resource_name NVARCHAR(512) NOT NULL,
                resource_type NVARCHAR(300) NOT NULL,
                resource_group NVARCHAR(255) NOT NULL,
                subscription_id NVARCHAR(100) NOT NULL,
                subscription_name NVARCHAR(255) NOT NULL,
                azure_resource_id NVARCHAR(2048) NULL,
                business_impact NVARCHAR(30) NOT NULL,
                additional_info NVARCHAR(MAX) NULL,
                first_seen_at DATETIME2 NOT NULL,
                last_seen_at DATETIME2 NOT NULL,
                status NVARCHAR(30) NOT NULL CONSTRAINT DF_waf_find_status DEFAULT 'active',
                resolved_at DATETIME2 NULL,
                CONSTRAINT FK_waf_find_rec FOREIGN KEY (recommendation_id) REFERENCES dbo.waf_recommendation(recommendation_id)
            );
            CREATE INDEX IX_waf_find_rec_status ON dbo.waf_resource_finding(recommendation_id, status);
        END
        """,
        // 4. tracking
        """
        IF OBJECT_ID('dbo.waf_recommendation_tracking', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.waf_recommendation_tracking (
                client_id INT NOT NULL,
                canonical_id INT NOT NULL,
                completion_pct INT NOT NULL CONSTRAINT DF_waf_track_pct DEFAULT 0,
                remediation_start_date DATE NULL,
                remediation_end_date DATE NULL,
                projected_bit_effort NVARCHAR(200) NULL,
                execution_log NVARCHAR(MAX) NULL,
                priority_override TINYINT NULL,
                internal_notes NVARCHAR(MAX) NULL,
                updated_at DATETIME2 NOT NULL CONSTRAINT DF_waf_track_updated DEFAULT SYSUTCDATETIME(),
                updated_by NVARCHAR(255) NULL,
                CONSTRAINT PK_waf_tracking PRIMARY KEY (client_id, canonical_id),
                CONSTRAINT CK_waf_track_pct CHECK (completion_pct BETWEEN 0 AND 100)
            );
        END
        """,
        // soft-migration tracking (tablas preexistentes): fecha de cierre
        """
        IF COL_LENGTH('dbo.waf_recommendation_tracking', 'remediation_end_date') IS NULL ALTER TABLE dbo.waf_recommendation_tracking ADD remediation_end_date DATE NULL;
        """,
        // 5. tracking_history
        """
        IF OBJECT_ID('dbo.waf_tracking_history', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.waf_tracking_history (
                history_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL,
                canonical_id INT NOT NULL,
                field_changed NVARCHAR(100) NOT NULL,
                old_value NVARCHAR(MAX) NULL,
                new_value NVARCHAR(MAX) NULL,
                changed_by NVARCHAR(255) NULL,
                changed_at DATETIME2 NOT NULL CONSTRAINT DF_waf_hist_changed DEFAULT SYSUTCDATETIME()
            );
            CREATE INDEX IX_waf_hist_client_canonical ON dbo.waf_tracking_history(client_id, canonical_id);
        END
        """,
        // 6. comment
        """
        IF OBJECT_ID('dbo.waf_recommendation_comment', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.waf_recommendation_comment (
                comment_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL,
                canonical_id INT NOT NULL,
                user_display NVARCHAR(255) NOT NULL,
                comment_text NVARCHAR(MAX) NOT NULL,
                created_at DATETIME2 NOT NULL CONSTRAINT DF_waf_comment_created DEFAULT SYSUTCDATETIME()
            );
            CREATE INDEX IX_waf_comment_client_canonical ON dbo.waf_recommendation_comment(client_id, canonical_id);
        END
        """,
        // 7. ingestion_run
        """
        IF OBJECT_ID('dbo.waf_ingestion_run', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.waf_ingestion_run (
                run_id INT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL,
                source_file_name NVARCHAR(400) NOT NULL,
                csv_export_date DATE NULL,
                rows_total INT NULL,
                rows_processed INT NULL,
                new_recommendations INT NULL,
                new_findings INT NULL,
                resolved_findings INT NULL,
                unmapped_recommendations NVARCHAR(MAX) NULL,
                subscription_results NVARCHAR(MAX) NULL,
                status NVARCHAR(30) NOT NULL CONSTRAINT DF_waf_run_status DEFAULT 'running',
                error_message NVARCHAR(MAX) NULL,
                started_at DATETIME2 NOT NULL CONSTRAINT DF_waf_run_started DEFAULT SYSUTCDATETIME(),
                completed_at DATETIME2 NULL,
                created_by NVARCHAR(255) NULL,
                CONSTRAINT FK_waf_run_client FOREIGN KEY (client_id) REFERENCES dbo.clients(client_id)
            );
            CREATE INDEX IX_waf_run_client_started ON dbo.waf_ingestion_run(client_id, started_at DESC);
        END
        """,
        // soft-migration ingestion_run (tablas preexistentes): resultado por suscripción de la corrida
        """
        IF COL_LENGTH('dbo.waf_ingestion_run', 'subscription_results') IS NULL ALTER TABLE dbo.waf_ingestion_run ADD subscription_results NVARCHAR(MAX) NULL;
        """,
        // 8. advisor_score_snapshot
        """
        IF OBJECT_ID('dbo.waf_advisor_score_snapshot', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.waf_advisor_score_snapshot (
                snapshot_id INT IDENTITY(1,1) PRIMARY KEY,
                client_id INT NOT NULL,
                snapshot_date DATE NOT NULL,
                captured_at DATETIME2 NOT NULL CONSTRAINT DF_waf_score_captured DEFAULT SYSUTCDATETIME(),
                status NVARCHAR(30) NOT NULL,
                has_connection BIT NOT NULL CONSTRAINT DF_waf_score_conn DEFAULT 0,
                subscriptions_scored INT NOT NULL CONSTRAINT DF_waf_score_subs DEFAULT 0,
                score_p1 DECIMAL(5,2) NULL, score_p2 DECIMAL(5,2) NULL, score_p3 DECIMAL(5,2) NULL,
                score_p4 DECIMAL(5,2) NULL, score_p5 DECIMAL(5,2) NULL,
                warnings_json NVARCHAR(MAX) NULL,
                breakdown_json NVARCHAR(MAX) NULL,
                source NVARCHAR(30) NOT NULL CONSTRAINT DF_waf_score_source DEFAULT 'manual',
                include_in_reports BIT NOT NULL CONSTRAINT DF_waf_score_reports DEFAULT 1,
                CONSTRAINT UQ_waf_score_snapshot_client_date_source UNIQUE (client_id, snapshot_date, source),
                CONSTRAINT FK_waf_score_client FOREIGN KEY (client_id) REFERENCES dbo.clients(client_id)
            );
            CREATE INDEX IX_waf_score_client_captured ON dbo.waf_advisor_score_snapshot(client_id, captured_at DESC);
        END
        """,
        // 9. canonical_alias
        """
        IF OBJECT_ID('dbo.waf_canonical_alias', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.waf_canonical_alias (
                alias_id INT IDENTITY(1,1) PRIMARY KEY,
                advisor_name NVARCHAR(500) NOT NULL,
                advisor_category NVARCHAR(100) NOT NULL,
                canonical_id INT NOT NULL,
                match_source NVARCHAR(30) NULL,
                confidence DECIMAL(5,4) NULL,
                created_by NVARCHAR(255) NULL,
                created_at DATETIME2 NOT NULL CONSTRAINT DF_waf_alias_created DEFAULT SYSUTCDATETIME(),
                CONSTRAINT FK_waf_alias_canonical FOREIGN KEY (canonical_id) REFERENCES dbo.waf_recommendation_canonical(canonical_id)
            );
            CREATE UNIQUE INDEX UX_waf_alias_signature ON dbo.waf_canonical_alias(advisor_name, advisor_category);
        END
        """,
        // 10. app_job_lock
        """
        IF OBJECT_ID('dbo.app_job_lock', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.app_job_lock (
                job_name NVARCHAR(100) NOT NULL PRIMARY KEY,
                locked_until DATETIME2 NOT NULL,
                owner NVARCHAR(255) NULL,
                updated_at DATETIME2 NOT NULL CONSTRAINT DF_app_job_lock_updated DEFAULT SYSUTCDATETIME()
            );
        END
        """,
    ];
}
