using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>
/// Persistencia de los insumos subidos. Cada carga REEMPLAZA la anterior de su tipo
/// (decisión "insumos vivos" del spec), así que no hace falta MERGE: DELETE + SqlBulkCopy
/// dentro de una transacción, lo que además hace la ingesta idempotente. Insertar fila a
/// fila está descartado: el módulo de Revisión de accesos midió ~106 ms por fila contra
/// Azure SQL, o sea unos 47 minutos para las 26.608 filas de un export real.
/// </summary>
public sealed class SqlInformeValorStore(ISqlConnectionFactory factory) : IInformeValorStore
{
    public const string KindFacturacion = "facturacion";
    public const string KindCasos = "casos";
    public const string KindRbac = "rbac";

    // OJO: client_id e ingesta_id son SIEMPRE las dos primeras columnas de las dos
    // proyecciones. ReplaceAsync las sobreescribe por posición (values[0] y values[1]),
    // porque su valor no sale de la fila sino de la corrida. Cambiar el orden las rompe,
    // y el test Las_columnas_de_facturacion_cubren_el_esquema fija esa secuencia.
    internal static readonly (string Column, Type Type, Func<FacturacionRow, object> Value)[] FacturacionColumns =
    [
        ("client_id", typeof(int), _ => 0),          // sobreescrita por ReplaceAsync
        ("ingesta_id", typeof(int), _ => 0),         // sobreescrita por ReplaceAsync
        ("natural_key_hash", typeof(string), r => r.Hash),
        ("tenant", typeof(string), r => Db(r.Tenant)),
        ("subscription_name", typeof(string), r => Db(r.SubscriptionName)),
        ("subscription_id", typeof(string), r => Db(r.SubscriptionId)),
        ("resource_group", typeof(string), r => Db(r.ResourceGroup)),
        ("resource_name", typeof(string), r => Db(r.ResourceName)),
        ("cost_center", typeof(string), r => Db(r.CostCenter)),
        ("category", typeof(string), r => Db(r.Category)),
        ("subcategory", typeof(string), r => Db(r.Subcategory)),
        ("service", typeof(string), r => Db(r.Service)),
        ("quantity", typeof(decimal), r => Db(r.Quantity)),
        ("unit", typeof(string), r => Db(r.Unit)),
        ("rate", typeof(decimal), r => Db(r.Rate)),
        ("pvp", typeof(decimal), r => r.Pvp),
        ("period_year", typeof(short), r => r.Year),
        ("period_month", typeof(byte), r => r.Month),
    ];

    internal static readonly (string Column, Type Type, Func<CasoRow, object> Value)[] CasoColumns =
    [
        ("client_id", typeof(int), _ => 0),
        ("ingesta_id", typeof(int), _ => 0),
        ("natural_key_hash", typeof(string), r => r.Hash),
        ("caso", typeof(string), r => Db(r.Caso)),
        ("fecha_registro", typeof(DateTime), r => r.FechaRegistro is null
            ? DBNull.Value : r.FechaRegistro.Value.ToDateTime(TimeOnly.MinValue)),
        ("estado", typeof(string), r => Db(r.Estado)),
        ("sla_horas", typeof(decimal), r => Db(r.SlaHoras)),
        ("duracion_cruda", typeof(decimal), r => Db(r.DuracionCruda)),
        ("cumple", typeof(string), r => Db(r.Cumple)),
        ("categoria", typeof(string), r => Db(r.Categoria)),
        ("subcategoria", typeof(string), r => Db(r.Subcategoria)),
        ("horario", typeof(string), r => Db(r.Horario)),
    ];

    public Task<int> ReplaceFacturacionAsync(
        int clientId, string fileName, string? user, ParseResult<FacturacionRow> parsed, CancellationToken ct) =>
        ReplaceAsync(clientId, KindFacturacion, "dbo.informe_valor_facturacion", fileName, user,
            parsed.Rows, FacturacionColumns, parsed, ct);

    public Task<int> ReplaceCasosAsync(
        int clientId, string fileName, string? user, ParseResult<CasoRow> parsed, CancellationToken ct) =>
        ReplaceAsync(clientId, KindCasos, "dbo.informe_valor_caso", fileName, user,
            parsed.Rows, CasoColumns, parsed, ct);

    private async Task<int> ReplaceAsync<T>(
        int clientId, string kind, string table, string fileName, string? user,
        IReadOnlyList<T> rows, (string Column, Type Type, Func<T, object> Value)[] columns,
        ParseResult<T> parsed, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await InformeValorSchema.EnsureSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var ingestaId = await CreateRunAsync(conn, tx, clientId, kind, fileName, user, ct);

            await ExecAsync(conn, tx, ct, $"DELETE FROM {table} WHERE client_id = @cid",
                ("@cid", SqlDbType.Int, clientId));

            using var data = new DataTable();
            foreach (var (column, type, _) in columns) data.Columns.Add(column, type).AllowDBNull = true;
            foreach (var row in rows)
            {
                var values = new object[columns.Length];
                for (var i = 0; i < columns.Length; i++) values[i] = columns[i].Value(row);
                values[0] = clientId;
                values[1] = ingestaId;
                data.Rows.Add(values);
            }

            if (data.Rows.Count > 0)
            {
                using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
                {
                    DestinationTableName = table, BatchSize = 2000, BulkCopyTimeout = 300,
                };
                foreach (DataColumn c in data.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
                await bulk.WriteToServerAsync(data, ct);
            }

            await FinishRunAsync(conn, tx, ingestaId, parsed, rows.Count, ct);
            await tx.CommitAsync(ct);
            return ingestaId;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task DeleteInsumoAsync(int clientId, string kind, CancellationToken ct)
    {
        var table = kind switch
        {
            KindFacturacion => "dbo.informe_valor_facturacion",
            KindCasos => "dbo.informe_valor_caso",
            KindRbac => "dbo.informe_valor_rbac",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await using var conn = await factory.OpenAsync(ct);
        await InformeValorSchema.EnsureSchemaAsync(conn, ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await ExecAsync(conn, tx, ct, $"DELETE FROM {table} WHERE client_id = @cid",
            ("@cid", SqlDbType.Int, clientId));
        await ExecAsync(conn, tx, ct,
            "DELETE FROM dbo.informe_valor_ingesta WHERE client_id = @cid AND kind = @k",
            ("@cid", SqlDbType.Int, clientId), ("@k", SqlDbType.NVarChar, kind));
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await InformeValorSchema.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.kind, i.source_file_name, i.completed_at, i.rows_processed, i.status, i.warnings_json
            FROM dbo.informe_valor_ingesta i
            INNER JOIN (
                SELECT kind, MAX(ingesta_id) AS ingesta_id
                FROM dbo.informe_valor_ingesta WHERE client_id = @cid GROUP BY kind
            ) u ON u.ingesta_id = i.ingesta_id
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", SqlDbType.Int) { Value = clientId });

        var result = new List<InsumoEstado>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var warnings = rd.IsDBNull(5)
                ? []
                : JsonSerializer.Deserialize<List<string>>(rd.GetString(5)) ?? [];
            result.Add(new InsumoEstado(
                rd.GetString(0), true,
                rd.IsDBNull(1) ? null : rd.GetString(1),
                rd.IsDBNull(2) ? null : rd.GetDateTime(2),
                rd.GetInt32(3),
                rd.IsDBNull(4) ? null : rd.GetString(4),
                warnings));
        }
        return result;
    }

    private static async Task<int> CreateRunAsync(
        SqlConnection conn, SqlTransaction tx, int clientId, string kind, string fileName, string? user,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO dbo.informe_valor_ingesta
                (client_id, kind, source_file_name, status, started_at, created_by)
            OUTPUT INSERTED.ingesta_id
            VALUES (@cid, @k, @f, 'running', @now, @u)
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", SqlDbType.Int) { Value = clientId });
        cmd.Parameters.Add(new SqlParameter("@k", SqlDbType.NVarChar, 20) { Value = kind });
        cmd.Parameters.Add(new SqlParameter("@f", SqlDbType.NVarChar, 400) { Value = TruncDb(fileName, 400) });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        cmd.Parameters.Add(new SqlParameter("@u", SqlDbType.NVarChar, 200) { Value = TruncDb(user, 200) });
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task FinishRunAsync<T>(
        SqlConnection conn, SqlTransaction tx, int ingestaId, ParseResult<T> parsed, int inserted,
        CancellationToken ct)
    {
        var warnings = parsed.Warnings.Count == 0 ? null : JsonSerializer.Serialize(parsed.Warnings);
        if (warnings is { Length: > 4000 }) warnings = warnings[..4000];

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE dbo.informe_valor_ingesta
            SET status = @s, rows_total = @tot, rows_processed = @proc, rows_skipped = @skip,
                truncated_values = @trunc, warnings_json = @w, completed_at = @now
            WHERE ingesta_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@s", SqlDbType.NVarChar, 30)
        { Value = parsed.Warnings.Count > 0 ? "completed_with_warnings" : "completed" });
        cmd.Parameters.Add(new SqlParameter("@tot", SqlDbType.Int) { Value = parsed.RowsTotal });
        cmd.Parameters.Add(new SqlParameter("@proc", SqlDbType.Int) { Value = inserted });
        cmd.Parameters.Add(new SqlParameter("@skip", SqlDbType.Int) { Value = parsed.RowsSkipped });
        cmd.Parameters.Add(new SqlParameter("@trunc", SqlDbType.Int) { Value = parsed.TruncatedValues });
        cmd.Parameters.Add(new SqlParameter("@w", SqlDbType.NVarChar, 4000) { Value = (object?)warnings ?? DBNull.Value });
        // Hora fresca: usar el @now del inicio dejaba Fin anterior a Inicio.
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = ingestaId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecAsync(
        SqlConnection conn, SqlTransaction tx, CancellationToken ct, string sql,
        params (string Name, SqlDbType Type, object Value)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, type, value) in ps)
            cmd.Parameters.Add(new SqlParameter(name, type) { Value = value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Un null de C# en SqlParameter lanza SqlException 8178; hay que mapear a DBNull.</summary>
    private static object Db(object? value) => value ?? DBNull.Value;

    /// <summary>
    /// Trunca al ancho de la columna y mapea null a DBNull.Value. Un valor más largo que su
    /// NVARCHAR(n) dispara el error 8152 de SQL Server y Kestrel corta la conexión en vez de
    /// devolver una respuesta HTTP normal — eso ya se leyó una vez como vulnerabilidad que no
    /// era. fileName y user pasan los dos por aquí antes del INSERT en CreateRunAsync.
    /// </summary>
    private static object TruncDb(string? value, int max) =>
        value is null ? DBNull.Value : value.Length > max ? value[..max] : value;
}
