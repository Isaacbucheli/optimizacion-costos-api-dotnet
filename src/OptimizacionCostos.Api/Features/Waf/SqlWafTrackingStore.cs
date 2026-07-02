using System.Globalization;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Implementación SQL del seguimiento WAF. Port fiel de app/waf/tracking.py
/// (<c>load_tracking</c> / <c>apply_tracking_update</c>) más las queries de historia y
/// comentarios que el FastAPI ejecuta inline en app/routes/waf.py.
///
/// Convenciones del repo: <c>ISqlConnectionFactory.OpenAsync</c>, SQL siempre parametrizado,
/// schema lazy (<c>WafSchema.EnsureWafSchemaAsync</c> al inicio de cada método público).
/// </summary>
public sealed class SqlWafTrackingStore(ISqlConnectionFactory factory) : IWafTrackingStore
{
    /// <summary>Campos rastreables (port de TRACKING_FIELDS de tracking.py). El orden importa
    /// para mapear y para el registro de historia.</summary>
    private static readonly string[] TrackingFields =
    [
        "completion_pct",
        "remediation_start_date",
        "remediation_end_date",
        "projected_bit_effort",
        "execution_log",
        "priority_override",
        "internal_notes",
    ];

    public async Task<WafTracking> LoadAsync(int clientId, int canonicalId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);
        return await LoadAsync(conn, null, clientId, canonicalId, ct);
    }

    /// <summary>Variante sobre una conexión/transacción ya abierta (uso interno desde ApplyUpdate).</summary>
    private static async Task<WafTracking> LoadAsync(
        SqlConnection conn, SqlTransaction? tx, int clientId, int canonicalId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT completion_pct, remediation_start_date, remediation_end_date, projected_bit_effort,
                   execution_log, priority_override, internal_notes, updated_at, updated_by
            FROM dbo.waf_recommendation_tracking
            WHERE client_id = @client_id AND canonical_id = @canonical_id
            """;
        cmd.Parameters.Add(new SqlParameter("@client_id", clientId));
        cmd.Parameters.Add(new SqlParameter("@canonical_id", canonicalId));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new WafTracking(
                CompletionPct: reader.GetInt32(0),
                RemediationStartDate: reader.IsDBNull(1)
                    ? null
                    : DateOnly.FromDateTime(reader.GetDateTime(1)),
                RemediationEndDate: reader.IsDBNull(2)
                    ? null
                    : DateOnly.FromDateTime(reader.GetDateTime(2)),
                ProjectedBitEffort: reader.IsDBNull(3) ? null : reader.GetString(3),
                ExecutionLog: reader.IsDBNull(4) ? null : reader.GetString(4),
                PriorityOverride: reader.IsDBNull(5) ? null : reader.GetByte(5),
                InternalNotes: reader.IsDBNull(6) ? null : reader.GetString(6),
                UpdatedAt: reader.IsDBNull(7) ? default : reader.GetDateTime(7),
                UpdatedBy: reader.IsDBNull(8) ? null : reader.GetString(8));
        }

        // Sin fila: defaults (paridad con el dict default de load_tracking).
        return new WafTracking(
            CompletionPct: 0,
            RemediationStartDate: null,
            RemediationEndDate: null,
            ProjectedBitEffort: null,
            ExecutionLog: null,
            PriorityOverride: null,
            InternalNotes: null,
            UpdatedAt: default,
            UpdatedBy: null);
    }

    public async Task<IReadOnlyList<string>> ApplyUpdateAsync(
        int clientId, int canonicalId, WafTrackingUpdate values, string? updatedBy, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        // El MERGE + los inserts de historia comparten una transacción (paridad con el cursor único
        // del FastAPI, que commitea todo junto).
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var current = await LoadAsync(conn, tx, clientId, canonicalId, ct);

            // clean_values: solo los campos efectivamente enviados (exclude_unset). Los None
            // presentes SÍ sobreescriben a NULL (igual que {**current, **clean_values}).
            var merged = new WafTracking(
                // completion_pct es NOT NULL en BD; el record lo modela como int no-nullable.
                // Si llegara enviado-pero-null (input sin sentido que el controller debe validar),
                // se cae a 0 en vez de violar el NOT NULL. El resto de campos sí admiten NULL.
                CompletionPct: values.CompletionPctSet ? (values.CompletionPct ?? 0) : current.CompletionPct,
                RemediationStartDate: values.RemediationStartDateSet ? values.RemediationStartDate : current.RemediationStartDate,
                RemediationEndDate: values.RemediationEndDateSet ? values.RemediationEndDate : current.RemediationEndDate,
                ProjectedBitEffort: values.ProjectedBitEffortSet ? values.ProjectedBitEffort : current.ProjectedBitEffort,
                ExecutionLog: values.ExecutionLogSet ? values.ExecutionLog : current.ExecutionLog,
                PriorityOverride: values.PriorityOverrideSet ? values.PriorityOverride : current.PriorityOverride,
                InternalNotes: values.InternalNotesSet ? values.InternalNotes : current.InternalNotes,
                UpdatedAt: current.UpdatedAt,
                UpdatedBy: current.UpdatedBy);

            await using (var merge = conn.CreateCommand())
            {
                merge.Transaction = tx;
                merge.CommandText = """
                    MERGE dbo.waf_recommendation_tracking AS target
                    USING (SELECT @client_id AS client_id, @canonical_id AS canonical_id) AS source
                    ON target.client_id = source.client_id AND target.canonical_id = source.canonical_id
                    WHEN MATCHED THEN
                        UPDATE SET completion_pct = @completion_pct, remediation_start_date = @remediation_start_date,
                            remediation_end_date = @remediation_end_date,
                            projected_bit_effort = @projected_bit_effort, execution_log = @execution_log,
                            priority_override = @priority_override, internal_notes = @internal_notes,
                            updated_at = SYSUTCDATETIME(), updated_by = @updated_by
                    WHEN NOT MATCHED THEN
                        INSERT (client_id, canonical_id, completion_pct, remediation_start_date,
                                remediation_end_date, projected_bit_effort, execution_log, priority_override,
                                internal_notes, updated_by)
                        VALUES (@client_id, @canonical_id, @completion_pct, @remediation_start_date,
                                @remediation_end_date, @projected_bit_effort, @execution_log, @priority_override,
                                @internal_notes, @updated_by);
                    """;
                merge.Parameters.Add(new SqlParameter("@client_id", clientId));
                merge.Parameters.Add(new SqlParameter("@canonical_id", canonicalId));
                merge.Parameters.Add(new SqlParameter("@completion_pct", merged.CompletionPct));
                merge.Parameters.Add(new SqlParameter("@remediation_start_date",
                    merged.RemediationStartDate is { } d ? d.ToDateTime(TimeOnly.MinValue) : DBNull.Value));
                merge.Parameters.Add(new SqlParameter("@remediation_end_date",
                    merged.RemediationEndDate is { } de ? de.ToDateTime(TimeOnly.MinValue) : DBNull.Value));
                merge.Parameters.Add(new SqlParameter("@projected_bit_effort", (object?)merged.ProjectedBitEffort ?? DBNull.Value));
                merge.Parameters.Add(new SqlParameter("@execution_log", (object?)merged.ExecutionLog ?? DBNull.Value));
                merge.Parameters.Add(new SqlParameter("@priority_override", (object?)merged.PriorityOverride ?? DBNull.Value));
                merge.Parameters.Add(new SqlParameter("@internal_notes", (object?)merged.InternalNotes ?? DBNull.Value));
                merge.Parameters.Add(new SqlParameter("@updated_by", (object?)updatedBy ?? DBNull.Value));
                await merge.ExecuteNonQueryAsync(ct);
            }

            // Historia: una fila por campo presente cuyo valor cambió (current != new). Recorre solo
            // los campos enviados, en el orden de TRACKING_FIELDS (paridad con la iteración de clean_values).
            var changed = new List<string>();
            foreach (var field in TrackingFields)
            {
                if (!TryGetSentValue(values, field, out var newValue)) continue; // no enviado → se ignora
                var oldValue = GetCurrentValue(current, field);
                if (Equals(oldValue, newValue)) continue; // mismo valor → no se registra

                changed.Add(field);
                await using var hist = conn.CreateCommand();
                hist.Transaction = tx;
                hist.CommandText = """
                    INSERT INTO dbo.waf_tracking_history (
                        client_id, canonical_id, field_changed, old_value, new_value, changed_by
                    )
                    VALUES (@client_id, @canonical_id, @field, @old_value, @new_value, @changed_by)
                    """;
                hist.Parameters.Add(new SqlParameter("@client_id", clientId));
                hist.Parameters.Add(new SqlParameter("@canonical_id", canonicalId));
                hist.Parameters.Add(new SqlParameter("@field", field));
                hist.Parameters.Add(new SqlParameter("@old_value", (object?)ToHistoryString(oldValue) ?? DBNull.Value));
                hist.Parameters.Add(new SqlParameter("@new_value", (object?)ToHistoryString(newValue) ?? DBNull.Value));
                hist.Parameters.Add(new SqlParameter("@changed_by", (object?)updatedBy ?? DBNull.Value));
                await hist.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return changed;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<WafTrackingHistory>> GetHistoryAsync(
        int clientId, int canonicalId, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (@limit)
                   history_id, client_id, canonical_id, field_changed,
                   old_value, new_value, changed_by, changed_at
            FROM dbo.waf_tracking_history
            WHERE client_id = @client_id AND canonical_id = @canonical_id
            ORDER BY changed_at DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@limit", limit));
        cmd.Parameters.Add(new SqlParameter("@client_id", clientId));
        cmd.Parameters.Add(new SqlParameter("@canonical_id", canonicalId));

        var rows = new List<WafTrackingHistory>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new WafTrackingHistory(
                HistoryId: reader.GetInt64(0),
                ClientId: reader.GetInt32(1),
                CanonicalId: reader.GetInt32(2),
                FieldChanged: reader.GetString(3),
                OldValue: reader.IsDBNull(4) ? null : reader.GetString(4),
                NewValue: reader.IsDBNull(5) ? null : reader.GetString(5),
                ChangedBy: reader.IsDBNull(6) ? null : reader.GetString(6),
                ChangedAt: reader.GetDateTime(7)));
        }
        return rows;
    }

    public async Task<long> AddCommentAsync(
        int clientId, int canonicalId, string commentText, string userDisplay, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.waf_recommendation_comment (client_id, canonical_id, user_display, comment_text)
            OUTPUT INSERTED.comment_id
            VALUES (@client_id, @canonical_id, @user_display, @comment_text)
            """;
        cmd.Parameters.Add(new SqlParameter("@client_id", clientId));
        cmd.Parameters.Add(new SqlParameter("@canonical_id", canonicalId));
        cmd.Parameters.Add(new SqlParameter("@user_display", userDisplay));
        cmd.Parameters.Add(new SqlParameter("@comment_text", commentText));

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<WafComment>> GetCommentsAsync(
        int clientId, int canonicalId, int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await WafSchema.EnsureWafSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (@limit)
                   comment_id, client_id, canonical_id, user_display, comment_text, created_at
            FROM dbo.waf_recommendation_comment
            WHERE client_id = @client_id AND canonical_id = @canonical_id
            ORDER BY created_at DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@limit", limit));
        cmd.Parameters.Add(new SqlParameter("@client_id", clientId));
        cmd.Parameters.Add(new SqlParameter("@canonical_id", canonicalId));

        var rows = new List<WafComment>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new WafComment(
                CommentId: reader.GetInt64(0),
                ClientId: reader.GetInt32(1),
                CanonicalId: reader.GetInt32(2),
                UserDisplay: reader.GetString(3),
                CommentText: reader.GetString(4),
                CreatedAt: reader.GetDateTime(5)));
        }
        return rows;
    }

    // ---------- Helpers de merge/historia ----------

    /// <summary>Devuelve el valor enviado para un campo (boxed) y true si vino en el JSON.</summary>
    private static bool TryGetSentValue(WafTrackingUpdate v, string field, out object? value)
    {
        switch (field)
        {
            case "completion_pct":
                value = v.CompletionPct; // valor enviado tal cual (puede ser null)
                return v.CompletionPctSet;
            case "remediation_start_date":
                value = v.RemediationStartDate;
                return v.RemediationStartDateSet;
            case "remediation_end_date":
                value = v.RemediationEndDate;
                return v.RemediationEndDateSet;
            case "projected_bit_effort":
                value = v.ProjectedBitEffort;
                return v.ProjectedBitEffortSet;
            case "execution_log":
                value = v.ExecutionLog;
                return v.ExecutionLogSet;
            case "priority_override":
                value = v.PriorityOverride;
                return v.PriorityOverrideSet;
            case "internal_notes":
                value = v.InternalNotes;
                return v.InternalNotesSet;
            default:
                value = null;
                return false;
        }
    }

    /// <summary>Valor actual (boxed) de un campo en el registro cargado.</summary>
    private static object? GetCurrentValue(WafTracking current, string field) => field switch
    {
        "completion_pct" => current.CompletionPct,
        "remediation_start_date" => current.RemediationStartDate,
        "remediation_end_date" => current.RemediationEndDate,
        "projected_bit_effort" => current.ProjectedBitEffort,
        "execution_log" => current.ExecutionLog,
        "priority_override" => current.PriorityOverride,
        "internal_notes" => current.InternalNotes,
        _ => null,
    };

    /// <summary>
    /// Conversión a string para old_value/new_value en historia (paridad con
    /// <c>str(value) if value is not None else None</c> de Python). NULL → null; lo demás → texto
    /// con InvariantCulture (DateOnly en ISO yyyy-MM-dd, igual que str(date) en Python).
    /// </summary>
    private static string? ToHistoryString(object? value) => value switch
    {
        null => null,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
