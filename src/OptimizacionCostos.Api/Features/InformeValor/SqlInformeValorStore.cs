using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.InformeValor.Entrega;
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
    public const string KindEvolucion = "evolucion";

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

    // RoleClass/IsCustomRole de RbacRow SÍ se proyectan (a diferencia de la entrega anterior: ver
    // el comentario de clase de RbacRow y las dos columnas de soft-migration en
    // InformeValorSchema): sin esto, todo rol privilegiado que llegara por el Excel de respaldo
    // perdía su clase al guardar y releer, y SeguridadCalculador lo contaba con el respaldo por
    // nombre en vez de por RoleClass -- InformeValorBulkColumnsTests fija que la lista de columnas
    // es exactamente la del esquema, incluidas estas dos.
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
        ("role_class", typeof(string), r => Db(r.RoleClass)),
        ("is_custom_role", typeof(bool), r => r.IsCustomRole),
    ];

    // Mismo criterio de columnas fijas por posición que las tres proyecciones de arriba (ver el
    // comentario sobre FacturacionColumns): client_id e ingesta_id primero, sobreescritas por
    // ReplaceAsync, y category/subcategory nulas (el balde residual de D1 del parser) mapeadas
    // a DBNull vía Db(), no a "".
    internal static readonly (string Column, Type Type, Func<EvolucionRow, object> Value)[] EvolucionColumns =
    [
        ("client_id", typeof(int), _ => 0),
        ("ingesta_id", typeof(int), _ => 0),
        ("natural_key_hash", typeof(string), r => r.NaturalKeyHash),
        ("category", typeof(string), r => Db(r.Category)),
        ("subcategory", typeof(string), r => Db(r.Subcategory)),
        ("resource_name", typeof(string), r => r.ResourceName),
        ("is_reservation", typeof(bool), r => r.IsReservation),
        ("pvp", typeof(decimal), r => r.Pvp),
        ("period_year", typeof(short), r => r.PeriodYear),
        ("period_month", typeof(byte), r => r.PeriodMonth),
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

    public Task<int> ReplaceEvolucionAsync(
        int clientId, string fileName, string? user, ParseResult<EvolucionRow> parsed, CancellationToken ct) =>
        ReplaceAsync(clientId, KindEvolucion, "dbo.informe_valor_evolucion", fileName, user,
            parsed.Rows, EvolucionColumns, parsed, ct);

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
            KindEvolucion => "dbo.informe_valor_evolucion",
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
                i.status, i.warnings_json, i.ingesta_id
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
                warnings,
                rd.GetInt32(7)));
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
                login, cuenta_activa, ultimo_login, role_class, is_custom_role
            FROM dbo.informe_valor_rbac
            WHERE client_id = @cid
            ORDER BY row_id
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", SqlDbType.Int) { Value = clientId });

        var result = new List<RbacFila>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            // RoleClass/IsCustomRole ya vienen de sus propias columnas (ver el comentario de
            // RbacColumns): RbacFilaConverter.Convertir es fiel a lo que reciba, y ahora lo que
            // recibe es lo mismo que calculó RbacParser al parsear, no null/false fijo.
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
                RoleClass: rd.IsDBNull(11) ? null : rd.GetString(11),
                IsCustomRole: rd.GetBoolean(12));
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

    /// <summary>Las filas de evolución de consumo ya persistidas de un cliente. A diferencia de
    /// <see cref="GetFacturacionAsync"/>/<see cref="GetCasosAsync"/>/<see cref="GetRbacAsync"/>,
    /// que ordenan por <c>row_id</c> (orden de inserción de la carga vigente), acá el consumo de
    /// la entrega 6 necesita las filas agrupadas por período, así que se ordena por
    /// año/mes/recurso directamente.</summary>
    public async Task<IReadOnlyList<EvolucionRow>> GetEvolucionAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await InformeValorSchema.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT natural_key_hash, category, subcategory, resource_name, is_reservation,
                pvp, period_year, period_month
            FROM dbo.informe_valor_evolucion
            WHERE client_id = @cid
            ORDER BY period_year, period_month, resource_name
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", SqlDbType.Int) { Value = clientId });

        var result = new List<EvolucionRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            result.Add(new EvolucionRow(
                NaturalKeyHash: rd.GetString(0),
                Category: rd.IsDBNull(1) ? null : rd.GetString(1),
                Subcategory: rd.IsDBNull(2) ? null : rd.GetString(2),
                ResourceName: rd.GetString(3),
                IsReservation: rd.GetBoolean(4),
                Pvp: rd.GetDecimal(5),
                PeriodYear: rd.GetInt16(6),
                PeriodMonth: rd.GetByte(7)));
        }
        return result;
    }

    // ===================================================================================
    // Bitácora de entregas (F4 de la entrega 3). Acumula: nunca borra la fila anterior del
    // mismo período, porque reemitir es legítimo y el historial importa.
    // ===================================================================================

    /// <summary>
    /// El bloque de columnas de trazabilidad, en un solo lugar. El INSERT y los dos SELECT de abajo
    /// lo comparten para que no pueda pasar lo de siempre: una columna que se escribe y nadie lee,
    /// o al revés. Si acá falta un campo que entra al cálculo, la entrega reemitida cambia de
    /// cifras sin que nada lo explique.
    /// </summary>
    /// <remarks>
    /// Una columna nueva va SIEMPRE al final de esta lista, aunque en el CREATE TABLE quede en otro
    /// lugar por legibilidad: los dos lectores de abajo van por índice posicional, así que meterla
    /// en el medio corre todos los índices que siguen y ninguna prueba lo cataría (se leería la
    /// columna de al lado, del mismo tipo, sin fallar).
    /// </remarks>
    private const string ColumnasEntregaCompleta = """
        entrega_id, period_start, period_end, corte, meses_parciales, variante,
        bloques_publicados, rbac_origen, rbac_corrida_fecha, seguridad_gestionada_externamente,
        facturacion_ingesta_id, casos_ingesta_id, rbac_ingesta_id, foto_reservas_json,
        plantilla_version, blob_name, blob_size_bytes, file_name, summary_json,
        generated_by, generated_at, blob_container
        """;

    public async Task<int> RegistrarEntregaAsync(EntregaNueva entrega, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await InformeValorSchema.EnsureSchemaAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.informe_valor_entrega
                (client_id, period_start, period_end, corte, meses_parciales, variante,
                 bloques_publicados, rbac_origen, rbac_corrida_fecha,
                 seguridad_gestionada_externamente, facturacion_ingesta_id, casos_ingesta_id,
                 rbac_ingesta_id, foto_reservas_json, plantilla_version, blob_container, blob_name,
                 blob_size_bytes, file_name, summary_json, generated_by, generated_at)
            OUTPUT INSERTED.entrega_id
            VALUES (@cid, @ini, @fin, @corte, @parc, @var, @bloques, @rbacOrigen, @rbacCorrida,
                    @segExt, @ingFact, @ingCasos, @ingRbac, @foto, @plantilla, @cont, @blob,
                    @size, @file, @summary, @by, @now)
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", SqlDbType.Int) { Value = entrega.ClientId });
        cmd.Parameters.Add(Fecha("@ini", entrega.PeriodStart));
        cmd.Parameters.Add(Fecha("@fin", entrega.PeriodEnd));
        cmd.Parameters.Add(Fecha("@corte", entrega.Corte));
        // El tri-estado viaja tal cual: NULL (sin declaración) y '[]' ("ningún mes parcial") son
        // dos entregas que reemiten distinto, así que no se colapsan.
        cmd.Parameters.Add(new SqlParameter("@parc", SqlDbType.NVarChar, 2000)
        { Value = (object?)MesesParcialesJson.Serializar(entrega.MesesParcialesForzados) ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@var", SqlDbType.NVarChar, 20) { Value = entrega.Variante.Clave() });
        cmd.Parameters.Add(new SqlParameter("@bloques", SqlDbType.NVarChar, 400)
        { Value = JsonSerializer.Serialize(entrega.BloquesPublicados.Select(b => b.Clave())) });
        cmd.Parameters.Add(new SqlParameter("@rbacOrigen", SqlDbType.NVarChar, 20)
        { Value = TruncDb(entrega.RbacOrigen, 20) });
        cmd.Parameters.Add(new SqlParameter("@rbacCorrida", SqlDbType.DateTime2)
        { Value = (object?)entrega.RbacCorridaFecha ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@segExt", SqlDbType.Bit) { Value = entrega.SeguridadGestionadaExternamente });
        cmd.Parameters.Add(Entero("@ingFact", entrega.FacturacionIngestaId));
        cmd.Parameters.Add(Entero("@ingCasos", entrega.CasosIngestaId));
        cmd.Parameters.Add(Entero("@ingRbac", entrega.RbacIngestaId));
        cmd.Parameters.Add(new SqlParameter("@foto", SqlDbType.NVarChar, -1)
        { Value = (object?)FotoReservasJson.Serializar(entrega.FotoReservas) ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@plantilla", SqlDbType.NVarChar, 64)
        { Value = TruncDb(entrega.PlantillaVersion, 64) });
        cmd.Parameters.Add(new SqlParameter("@cont", SqlDbType.NVarChar, 200) { Value = TruncDb(entrega.BlobContainer, 200) });
        // OJO: TruncDb corta a 400. El nombre del blob que se guarda tiene que ser IDÉNTICO al que
        // se subió, o la descarga busca un blob que no existe: por eso InformeValorController lo
        // arma con largo fijo y sin el nombre del archivo de descarga, que sí puede ser largo.
        cmd.Parameters.Add(new SqlParameter("@blob", SqlDbType.NVarChar, 400) { Value = TruncDb(entrega.BlobName, 400) });
        cmd.Parameters.Add(new SqlParameter("@size", SqlDbType.Int) { Value = entrega.BlobSizeBytes });
        cmd.Parameters.Add(new SqlParameter("@file", SqlDbType.NVarChar, 400) { Value = TruncDb(entrega.FileName, 400) });
        cmd.Parameters.Add(new SqlParameter("@summary", SqlDbType.NVarChar, -1)
        { Value = (object?)entrega.SummaryJson ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@by", SqlDbType.NVarChar, 200) { Value = TruncDb(entrega.GeneratedBy, 200) });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });

        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<EntregaResumen>> GetEntregasAsync(int clientId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await InformeValorSchema.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {ColumnasEntregaCompleta}
            FROM dbo.informe_valor_entrega
            WHERE client_id = @cid
            ORDER BY generated_at DESC, entrega_id DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", SqlDbType.Int) { Value = clientId });

        var result = new List<EntregaResumen>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) result.Add(LeerResumen(rd));
        return result;
    }

    public async Task<EntregaArchivada?> GetEntregaAsync(int clientId, int entregaId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await InformeValorSchema.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        // client_id va en el WHERE, no en una comparación posterior: un entrega_id adivinado no
        // puede devolver el artefacto de otro cliente.
        cmd.CommandText = $"""
            SELECT {ColumnasEntregaCompleta}
            FROM dbo.informe_valor_entrega
            WHERE client_id = @cid AND entrega_id = @eid
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", SqlDbType.Int) { Value = clientId });
        cmd.Parameters.Add(new SqlParameter("@eid", SqlDbType.Int) { Value = entregaId });

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;

        return new EntregaArchivada(
            Resumen: LeerResumen(rd),
            // Índice 21: última columna de ColumnasEntregaCompleta (ver su remarks).
            BlobContainer: rd.IsDBNull(21) ? null : rd.GetString(21),
            BlobName: rd.GetString(15),
            MesesParcialesForzados: MesesParcialesJson.Deserializar(rd.IsDBNull(4) ? null : rd.GetString(4)),
            RbacCorridaFecha: rd.IsDBNull(8) ? null : rd.GetDateTime(8),
            SeguridadGestionadaExternamente: rd.GetBoolean(9),
            FacturacionIngestaId: rd.IsDBNull(10) ? null : rd.GetInt32(10),
            CasosIngestaId: rd.IsDBNull(11) ? null : rd.GetInt32(11),
            RbacIngestaId: rd.IsDBNull(12) ? null : rd.GetInt32(12),
            FotoReservas: FotoReservasJson.Deserializar(rd.IsDBNull(13) ? null : rd.GetString(13)),
            PlantillaVersion: rd.IsDBNull(14) ? null : rd.GetString(14),
            SummaryJson: rd.IsDBNull(18) ? null : rd.GetString(18));
    }

    /// <summary>Las columnas del resumen, leídas por el MISMO orden posicional de
    /// <see cref="ColumnasEntregaCompleta"/> desde los dos SELECT.</summary>
    private static EntregaResumen LeerResumen(SqlDataReader rd) => new(
        EntregaId: rd.GetInt32(0),
        PeriodStart: DateOnly.FromDateTime(rd.GetDateTime(1)),
        PeriodEnd: DateOnly.FromDateTime(rd.GetDateTime(2)),
        Corte: DateOnly.FromDateTime(rd.GetDateTime(3)),
        Variante: rd.GetString(5),
        BloquesPublicados: JsonSerializer.Deserialize<List<string>>(rd.GetString(6)) ?? [],
        RbacOrigen: rd.IsDBNull(7) ? null : rd.GetString(7),
        FileName: rd.GetString(17),
        BlobSizeBytes: rd.GetInt32(16),
        GeneratedBy: rd.IsDBNull(19) ? null : rd.GetString(19),
        GeneratedAt: rd.GetDateTime(20));

    private static SqlParameter Fecha(string nombre, DateOnly valor) =>
        new(nombre, SqlDbType.Date) { Value = valor.ToDateTime(TimeOnly.MinValue) };

    private static SqlParameter Entero(string nombre, int? valor) =>
        new(nombre, SqlDbType.Int) { Value = (object?)valor ?? DBNull.Value };

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
