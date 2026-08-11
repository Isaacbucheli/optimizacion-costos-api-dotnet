using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

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

    // RoleClass/IsCustomRole de RbacRow NO aparecen acá: informe_valor_rbac no tiene columnas
    // para ellos (ver el comentario de clase de RbacRow) y esta tarea no habilita tocar el
    // esquema. Se calculan igual al parsear (para quien use RbacParser.Parse directo, antes de
    // guardar) pero no sobreviven este bulk copy — InformeValorBulkColumnsTests fija que la
    // lista de columnas es exactamente la del esquema, sin ellos.
    internal static readonly (string Column, Type Type, Func<RbacRow, object> Value)[] RbacColumns =
    [
        ("client_id", typeof(int), _ => 0),
        ("ingesta_id", typeof(int), _ => 0),
        ("natural_key_hash", typeof(string), r => r.Hash),
        ("sheet_name", typeof(string), r => Db(r.SheetName)),
        ("suscripcion", typeof(string), r => Db(r.Suscripcion)),
        ("scope", typeof(string), r => Db(r.Scope)),
        ("nivel", typeof(string), r => Db(r.Nivel)),
        ("rol", typeof(string), r => Db(r.Rol)),
        ("tipo", typeof(string), r => Db(r.Tipo)),
        ("nombre", typeof(string), r => Db(r.Nombre)),
        ("login", typeof(string), r => Db(r.Login)),
        ("cuenta_activa", typeof(string), r => Db(r.CuentaActiva)),
        ("ultimo_login", typeof(string), r => Db(r.UltimoLogin)),
    ];

    public Task<int> ReplaceFacturacionAsync(
        int clientId, string fileName, string? user, ParseResult<FacturacionRow> parsed, CancellationToken ct) =>
        ReplaceAsync(clientId, KindFacturacion, "dbo.informe_valor_facturacion", fileName, user,
            parsed.Rows, FacturacionColumns, parsed, ct);

    public Task<int> ReplaceCasosAsync(
        int clientId, string fileName, string? user, ParseResult<CasoRow> parsed, CancellationToken ct) =>
        ReplaceAsync(clientId, KindCasos, "dbo.informe_valor_caso", fileName, user,
            parsed.Rows, CasoColumns, parsed, ct);

    public Task<int> ReplaceRbacAsync(
        int clientId, string fileName, string? user, RbacParseResult parsed, CancellationToken ct) =>
        ReplaceAsync(clientId, KindRbac, "dbo.informe_valor_rbac", fileName, user,
            parsed.Rows, RbacColumns,
            // RowsMerged siempre 0: RbacParser nunca fusiona (ver su comentario de clase), a
            // diferencia de BitcostParser. ToGeneric solo re-empaqueta los campos que
            // FinishRunAsync<T> necesita; HojaLeida/HojasIgnoradas/Ejes/SinIdentificar ya están
            // reflejados en parsed.Warnings.
            new ParseResult<RbacRow>(parsed.Rows, parsed.RowsTotal, parsed.RowsSkipped, 0, parsed.TruncatedValues, parsed.Warnings),
            ct);

    private async Task<int> ReplaceAsync<T>(
        int clientId, string kind, string table, string fileName, string? user,
        IReadOnlyList<T> rows, (string Column, Type Type, Func<T, object> Value)[] columns,
        ParseResult<T> parsed, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await InformeValorSchema.EnsureSchemaAsync(conn, ct);

        // La fila de la corrida se crea FUERA de la transacción de datos, a propósito: si el
        // DELETE + bulk insert de abajo revienta y esa transacción hace rollback, esta fila
        // tiene que sobrevivirlo. Es la bitácora de que se intentó cargar algo y qué pasó (así
        // lo pide el spec de informe_valor_ingesta); sin ella, una carga que falla no deja
        // ningún rastro que el consultor pueda mirar después. Partirla en dos pasos no le pega
        // a la atomicidad de los DATOS: la corrida es metadata sobre el intento, no una fila de
        // facturación o casos que tenga que aparecer o desaparecer junto con ellas.
        var ingestaId = await CreateRunAsync(conn, null, clientId, kind, fileName, user, ct);
        try
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
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
        catch (Exception ex)
        {
            // Ya no queda transacción de datos que rollbackear (se deshizo arriba, si llegó a
            // abrirse: esto también atrapa que BeginTransactionAsync mismo falle). MarkRunFailedAsync
            // usa "conn" directo, sin transacción, así que esta escritura sobrevive igual.
            await MarkRunFailedAsync(conn, ingestaId, ex, ct);
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
        try
        {
            await ExecAsync(conn, tx, ct, $"DELETE FROM {table} WHERE client_id = @cid",
                ("@cid", SqlDbType.Int, clientId));
            await ExecAsync(conn, tx, ct,
                "DELETE FROM dbo.informe_valor_ingesta WHERE client_id = @cid AND kind = @k",
                ("@cid", SqlDbType.Int, clientId), ("@k", SqlDbType.NVarChar, kind));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<InsumoEstado>> GetEstadoAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await InformeValorSchema.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.kind, i.source_file_name, i.completed_at, i.rows_processed, i.rows_merged,
                i.status, i.warnings_json
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
            var warnings = rd.IsDBNull(6)
                ? []
                : JsonSerializer.Deserialize<List<string>>(rd.GetString(6)) ?? [];
            result.Add(new InsumoEstado(
                rd.GetString(0), true,
                rd.IsDBNull(1) ? null : rd.GetString(1),
                rd.IsDBNull(2) ? null : rd.GetDateTime(2),
                rd.GetInt32(3),
                rd.GetInt32(4),
                rd.IsDBNull(5) ? null : rd.GetString(5),
                warnings));
        }
        return result;
    }

    public async Task<IReadOnlyList<FacturacionRow>> GetFacturacionAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await InformeValorSchema.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT natural_key_hash, tenant, subscription_name, subscription_id, resource_group,
                resource_name, cost_center, category, subcategory, service, quantity, unit, rate,
                pvp, period_year, period_month
            FROM dbo.informe_valor_facturacion
            WHERE client_id = @cid
            ORDER BY row_id
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", SqlDbType.Int) { Value = clientId });

        var result = new List<FacturacionRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            result.Add(new FacturacionRow(
                Hash: rd.GetString(0),
                Tenant: rd.IsDBNull(1) ? null : rd.GetString(1),
                SubscriptionName: rd.IsDBNull(2) ? null : rd.GetString(2),
                SubscriptionId: rd.IsDBNull(3) ? null : rd.GetString(3),
                ResourceGroup: rd.IsDBNull(4) ? null : rd.GetString(4),
                ResourceName: rd.IsDBNull(5) ? null : rd.GetString(5),
                CostCenter: rd.IsDBNull(6) ? null : rd.GetString(6),
                Category: rd.IsDBNull(7) ? null : rd.GetString(7),
                Subcategory: rd.IsDBNull(8) ? null : rd.GetString(8),
                Service: rd.IsDBNull(9) ? null : rd.GetString(9),
                Quantity: rd.IsDBNull(10) ? null : rd.GetDecimal(10),
                Unit: rd.IsDBNull(11) ? null : rd.GetString(11),
                Rate: rd.IsDBNull(12) ? null : rd.GetDecimal(12),
                Pvp: rd.GetDecimal(13),
                Year: rd.GetInt16(14),
                Month: rd.GetByte(15)));
        }
        return result;
    }

    public async Task<IReadOnlyList<RbacFila>> GetRbacAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await InformeValorSchema.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT natural_key_hash, sheet_name, suscripcion, scope, nivel, rol, tipo, nombre,
                login, cuenta_activa, ultimo_login
            FROM dbo.informe_valor_rbac
            WHERE client_id = @cid
            ORDER BY row_id
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", SqlDbType.Int) { Value = clientId });

        var result = new List<RbacFila>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            // RoleClass/IsCustomRole en null/false SIEMPRE acá: la columna no existe en la
            // consulta de arriba (no hay de dónde leerlos). RbacFilaConverter.Convertir en sí
            // mismo es fiel a lo que reciba; la pérdida es de esta lectura, no de esa conversión
            // (ver el comentario de clase de RbacFilaConverter).
            var row = new RbacRow(
                Hash: rd.GetString(0),
                SheetName: rd.IsDBNull(1) ? null : rd.GetString(1),
                Suscripcion: rd.IsDBNull(2) ? null : rd.GetString(2),
                Scope: rd.IsDBNull(3) ? null : rd.GetString(3),
                Nivel: rd.IsDBNull(4) ? null : rd.GetString(4),
                Rol: rd.IsDBNull(5) ? null : rd.GetString(5),
                Tipo: rd.IsDBNull(6) ? null : rd.GetString(6),
                Nombre: rd.IsDBNull(7) ? null : rd.GetString(7),
                Login: rd.IsDBNull(8) ? null : rd.GetString(8),
                CuentaActiva: rd.IsDBNull(9) ? null : rd.GetString(9),
                UltimoLogin: rd.IsDBNull(10) ? null : rd.GetString(10),
                RoleClass: null, IsCustomRole: false);
            result.Add(RbacFilaConverter.Convertir(row));
        }
        return result;
    }

    public async Task<IReadOnlyList<CasoRow>> GetCasosAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await InformeValorSchema.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT natural_key_hash, caso, fecha_registro, estado, sla_horas, duracion_cruda,
                cumple, categoria, subcategoria, horario
            FROM dbo.informe_valor_caso
            WHERE client_id = @cid
            ORDER BY row_id
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", SqlDbType.Int) { Value = clientId });

        var result = new List<CasoRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            result.Add(new CasoRow(
                Hash: rd.GetString(0),
                Caso: rd.IsDBNull(1) ? null : rd.GetString(1),
                FechaRegistro: rd.IsDBNull(2) ? null : DateOnly.FromDateTime(rd.GetDateTime(2)),
                Estado: rd.IsDBNull(3) ? null : rd.GetString(3),
                SlaHoras: rd.IsDBNull(4) ? null : rd.GetDecimal(4),
                DuracionCruda: rd.IsDBNull(5) ? null : rd.GetDecimal(5),
                Cumple: rd.IsDBNull(6) ? null : rd.GetString(6),
                Categoria: rd.IsDBNull(7) ? null : rd.GetString(7),
                Subcategoria: rd.IsDBNull(8) ? null : rd.GetString(8),
                Horario: rd.IsDBNull(9) ? null : rd.GetString(9)));
        }
        return result;
    }

    private static async Task<int> CreateRunAsync(
        SqlConnection conn, SqlTransaction? tx, int clientId, string kind, string fileName, string? user,
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
                rows_merged = @merged, truncated_values = @trunc, warnings_json = @w, completed_at = @now
            WHERE ingesta_id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@s", SqlDbType.NVarChar, 30)
        { Value = parsed.Warnings.Count > 0 ? "completed_with_warnings" : "completed" });
        cmd.Parameters.Add(new SqlParameter("@tot", SqlDbType.Int) { Value = parsed.RowsTotal });
        cmd.Parameters.Add(new SqlParameter("@proc", SqlDbType.Int) { Value = inserted });
        cmd.Parameters.Add(new SqlParameter("@skip", SqlDbType.Int) { Value = parsed.RowsSkipped });
        cmd.Parameters.Add(new SqlParameter("@merged", SqlDbType.Int) { Value = parsed.RowsMerged });
        cmd.Parameters.Add(new SqlParameter("@trunc", SqlDbType.Int) { Value = parsed.TruncatedValues });
        cmd.Parameters.Add(new SqlParameter("@w", SqlDbType.NVarChar, 4000) { Value = (object?)warnings ?? DBNull.Value });
        // Hora fresca: usar el @now del inicio dejaba Fin anterior a Inicio.
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = ingestaId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Contraparte de FinishRunAsync para el camino de error: marca la corrida como fallida con
    /// su mensaje, SIN transacción (conn directo), porque se llama después de que la transacción
    /// de datos ya hizo rollback. Es mejor esfuerzo a propósito: si ni esto se puede escribir
    /// (p. ej. la misma caída de conexión que tumbó la carga), no hay que tapar la excepción
    /// original que ReplaceAsync relanza con throw justo después de llamarla.
    /// Internal para que el test sin base de datos (conexión nunca abierta) pueda invocarla directo.
    /// </summary>
    internal static async Task MarkRunFailedAsync(
        SqlConnection conn, int ingestaId, Exception ex, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE dbo.informe_valor_ingesta
                SET status = 'error', error_message = @err, completed_at = @now
                WHERE ingesta_id = @id
                """;
            cmd.Parameters.Add(new SqlParameter("@err", SqlDbType.NVarChar, 1000) { Value = TruncDb(ex.Message, 1000) });
            cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = ingestaId });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Nada mejor que hacer acá: que se propague la excepción ORIGINAL (la de "ex", vía
            // throw; en el catch de ReplaceAsync), no una nueva sobre el intento de registrar el
            // fallo.
        }
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
    /// era. fileName y user pasan los dos por aquí antes del INSERT en CreateRunAsync, y
    /// ex.Message también antes del UPDATE de MarkRunFailedAsync.
    /// </summary>
    private static object TruncDb(string? value, int max) =>
        value is null ? DBNull.Value : value.Length > max ? value[..max] : value;
}
