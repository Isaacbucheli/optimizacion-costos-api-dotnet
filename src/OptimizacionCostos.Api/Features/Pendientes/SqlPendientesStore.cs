using System.Data;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Pendientes;

/// <summary>
/// Implementación SQL contra la BD del tablero (Seguimiento CDC). Deliberadamente SIN EnsureSchema:
/// el esquema es de otro sistema y la app no hace DDL acá.
///
/// Las escrituras son granulares porque la SWA del tablero escribe la misma base en paralelo:
/// agregar una nota es un INSERT en Historial (no una reescritura del pendiente) y la edición del
/// pendiente va con concurrencia optimista por <c>Actualizado</c>.
/// </summary>
public sealed class SqlPendientesStore(ISeguimientoSqlConnectionFactory factory) : IPendientesStore
{
    /// <summary>Prefijo de los Ids que genera la plataforma, distinto del de la SWA (<c>pm…</c>).</summary>
    private const string IdPrefix = "pl";

    private const string ClienteColumns = "Num, Cliente, Servicio, Categoria, Pais, Coordinador, Consultor";

    private const string ItemColumns =
        "Id, ClienteNum, Titulo, Descripcion, Tipo, Prioridad, Estado, Responsable, FechaCreacion, Actualizado";

    // ---------- Lectura ----------

    public async Task<PendientesPayload> GetAreaAsync(string area, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        var clientes = new List<PendienteCliente>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {ClienteColumns} FROM dbo.Cliente WHERE Area = @area ORDER BY Num";
            cmd.Parameters.Add(new SqlParameter("@area", area));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) clientes.Add(MapCliente(reader));
        }

        // Notas primero: se agrupan por pendiente y se cuelgan del item al mapear.
        var notas = new Dictionary<string, List<PendienteNota>>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT h.PendienteId, h.HistId, h.Fecha, h.Nota, h.Autor, h.Orden
                FROM dbo.Historial h
                INNER JOIN dbo.Pendiente p ON p.Id = h.PendienteId
                WHERE p.Area = @area
                ORDER BY h.PendienteId, h.Orden, h.HistId
                """;
            cmd.Parameters.Add(new SqlParameter("@area", area));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var pendienteId = reader.GetString(0);
                if (!notas.TryGetValue(pendienteId, out var list))
                {
                    list = [];
                    notas[pendienteId] = list;
                }
                list.Add(new PendienteNota
                {
                    HistId = reader.GetInt32(1),
                    Fecha = ReadDateOnly(reader, 2),
                    Nota = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Autor = ReadNullableString(reader, 4),
                    Orden = reader.GetInt32(5),
                });
            }
        }

        var pendientes = new List<PendienteItem>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"SELECT {ItemColumns} FROM dbo.Pendiente WHERE Area = @area ORDER BY Actualizado DESC, Id";
            cmd.Parameters.Add(new SqlParameter("@area", area));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var item = MapItem(reader);
                pendientes.Add(notas.TryGetValue(item.Id, out var list)
                    ? item with { Historial = list }
                    : item);
            }
        }

        return new PendientesPayload { Area = area, Clientes = clientes, Pendientes = pendientes };
    }

    public async Task<PendienteItem?> GetItemAsync(string area, string id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        PendienteItem? item = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {ItemColumns} FROM dbo.Pendiente WHERE Area = @area AND Id = @id";
            cmd.Parameters.Add(new SqlParameter("@area", area));
            cmd.Parameters.Add(new SqlParameter("@id", id));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) item = MapItem(reader);
        }
        if (item is null) return null;

        var notas = new List<PendienteNota>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT HistId, Fecha, Nota, Autor, Orden
                FROM dbo.Historial
                WHERE PendienteId = @id
                ORDER BY Orden, HistId
                """;
            cmd.Parameters.Add(new SqlParameter("@id", id));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                notas.Add(new PendienteNota
                {
                    HistId = reader.GetInt32(0),
                    Fecha = ReadDateOnly(reader, 1),
                    Nota = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Autor = ReadNullableString(reader, 3),
                    Orden = reader.GetInt32(4),
                });
        }

        return item with { Historial = notas };
    }

    // ---------- Escritura de pendientes ----------

    public async Task<string> CreateItemAsync(string area, PendienteWrite data, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        // La SWA genera ids con su propio prefijo; una colisión es improbable pero el PK la cortaría,
        // así que se reintenta con un id nuevo antes de dejar salir el error.
        for (var attempt = 0; ; attempt++)
        {
            var id = NewId();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO dbo.Pendiente
                        (Id, Area, ClienteNum, Titulo, Descripcion, Tipo, Prioridad, Estado,
                         Responsable, FechaCreacion, Actualizado)
                    VALUES
                        (@id, @area, @clienteNum, @titulo, @descripcion, @tipo, @prioridad, @estado,
                         @responsable, @fechaCreacion, SYSUTCDATETIME())
                    """;
                cmd.Parameters.Add(new SqlParameter("@id", id));
                cmd.Parameters.Add(new SqlParameter("@area", area));
                AddItemFields(cmd, data);
                cmd.Parameters.Add(new SqlParameter("@fechaCreacion", SqlDbType.Date)
                { Value = DateTime.UtcNow.Date });
                await cmd.ExecuteNonQueryAsync(ct);
                return id;
            }
            catch (SqlException ex) when (IsDuplicateKey(ex) && attempt < 2)
            {
                // reintenta con otro id
            }
        }
    }

    public async Task<WriteOutcome> UpdateItemAsync(
        string area, string id, PendienteWrite data, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE dbo.Pendiente
                   SET ClienteNum = @clienteNum, Titulo = @titulo, Descripcion = @descripcion,
                       Tipo = @tipo, Prioridad = @prioridad, Estado = @estado,
                       Responsable = @responsable, Actualizado = SYSUTCDATETIME()
                 WHERE Id = @id AND Area = @area AND Actualizado = @actualizado
                """;
            cmd.Parameters.Add(new SqlParameter("@id", id));
            cmd.Parameters.Add(new SqlParameter("@area", area));
            // DateTime2 EXPLÍCITO: la columna es datetime2(7) y el constructor que infiere el tipo a
            // partir del valor manda un `datetime` (precisión ~3 ms), así que el WHERE nunca calzaba y
            // toda edición terminaba en 409. Verificado contra la BD real (2026-07-28).
            cmd.Parameters.Add(new SqlParameter("@actualizado", SqlDbType.DateTime2)
            { Value = (object?)data.Actualizado ?? DBNull.Value });
            AddItemFields(cmd, data);
            if (await cmd.ExecuteNonQueryAsync(ct) > 0) return WriteOutcome.Ok;
        }

        // 0 filas: o el pendiente no existe en el área, o alguien más lo cambió (la SWA, otra pestaña).
        return await ItemExistsAsync(conn, area, id, ct) ? WriteOutcome.Conflict : WriteOutcome.NotFound;
    }

    public async Task<bool> DeleteItemAsync(string area, string id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        if (!await ItemExistsAsync(conn, area, id, ct, tx))
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM dbo.Historial WHERE PendienteId = @id";
            cmd.Parameters.Add(new SqlParameter("@id", id));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        int deleted;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM dbo.Pendiente WHERE Id = @id AND Area = @area";
            cmd.Parameters.Add(new SqlParameter("@id", id));
            cmd.Parameters.Add(new SqlParameter("@area", area));
            deleted = await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return deleted > 0;
    }

    // ---------- Notas ----------

    public async Task<int?> AddNotaAsync(
        string area, string id, NotaWrite data, string? autor, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        if (!await ItemExistsAsync(conn, area, id, ct, tx))
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        int histId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // Orden = MAX+1, nunca COUNT(*): si alguna vez quedan huecos, COUNT repetiría un Orden.
            cmd.CommandText = """
                INSERT INTO dbo.Historial (PendienteId, Fecha, Nota, Autor, Orden)
                OUTPUT INSERTED.HistId
                SELECT @id, @fecha, @nota, @autor, ISNULL(MAX(h.Orden), -1) + 1
                  FROM dbo.Historial h
                 WHERE h.PendienteId = @id
                """;
            cmd.Parameters.Add(new SqlParameter("@id", id));
            cmd.Parameters.Add(new SqlParameter("@fecha", SqlDbType.Date)
            { Value = (data.Fecha ?? DateOnly.FromDateTime(DateTime.UtcNow)).ToDateTime(TimeOnly.MinValue) });
            cmd.Parameters.Add(new SqlParameter("@nota", data.Nota));
            cmd.Parameters.Add(new SqlParameter("@autor", (object?)NullIfBlank(autor) ?? DBNull.Value));
            histId = (int)(await cmd.ExecuteScalarAsync(ct))!;
        }

        await TouchItemAsync(conn, tx, id, ct);
        await tx.CommitAsync(ct);
        return histId;
    }

    public async Task<bool> DeleteNotaAsync(string area, string id, int histId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        if (!await ItemExistsAsync(conn, area, id, ct, tx))
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        int deleted;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // PendienteId en el WHERE: un HistId de otro pendiente no se borra "por casualidad".
            cmd.CommandText = "DELETE FROM dbo.Historial WHERE HistId = @histId AND PendienteId = @id";
            cmd.Parameters.Add(new SqlParameter("@histId", histId));
            cmd.Parameters.Add(new SqlParameter("@id", id));
            deleted = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (deleted == 0)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        await TouchItemAsync(conn, tx, id, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // ---------- Catálogo de clientes ----------

    public async Task<bool> ClienteExistsAsync(string area, int num, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM dbo.Cliente WHERE Area = @area AND Num = @num";
        cmd.Parameters.Add(new SqlParameter("@area", area));
        cmd.Parameters.Add(new SqlParameter("@num", num));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<int> CreateClienteAsync(string area, ClienteWrite data, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);

        // MAX(Num)+1 dentro del área. La SWA puede insertar al mismo tiempo: el PK (Area, Num) corta el
        // choque y se reintenta.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO dbo.Cliente (Area, Num, Cliente, Servicio, Categoria, Pais, Coordinador, Consultor)
                    OUTPUT INSERTED.Num
                    SELECT @area, ISNULL(MAX(c.Num), 0) + 1, @cliente, @servicio, @categoria, @pais,
                           @coordinador, @consultor
                      FROM dbo.Cliente c
                     WHERE c.Area = @area
                    """;
                cmd.Parameters.Add(new SqlParameter("@area", area));
                AddClienteFields(cmd, data);
                return (int)(await cmd.ExecuteScalarAsync(ct))!;
            }
            catch (SqlException ex) when (IsDuplicateKey(ex) && attempt < 2)
            {
                // reintenta: otro proceso tomó ese Num
            }
        }
    }

    public async Task<bool> UpdateClienteAsync(
        string area, int num, ClienteWrite data, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dbo.Cliente
               SET Cliente = @cliente, Servicio = @servicio, Categoria = @categoria, Pais = @pais,
                   Coordinador = @coordinador, Consultor = @consultor
             WHERE Area = @area AND Num = @num
            """;
        cmd.Parameters.Add(new SqlParameter("@area", area));
        cmd.Parameters.Add(new SqlParameter("@num", num));
        AddClienteFields(cmd, data);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<ClienteDeleteOutcome> DeleteClienteAsync(
        string area, int num, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // No hay FK en esta BD: la integridad la cuida la app. Nunca cascada.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT 1 FROM dbo.Pendiente WHERE Area = @area AND ClienteNum = @num";
            cmd.Parameters.Add(new SqlParameter("@area", area));
            cmd.Parameters.Add(new SqlParameter("@num", num));
            if (await cmd.ExecuteScalarAsync(ct) is not null)
            {
                await tx.RollbackAsync(ct);
                return ClienteDeleteOutcome.HasPendientes;
            }
        }

        int deleted;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM dbo.Cliente WHERE Area = @area AND Num = @num";
            cmd.Parameters.Add(new SqlParameter("@area", area));
            cmd.Parameters.Add(new SqlParameter("@num", num));
            deleted = await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return deleted > 0 ? ClienteDeleteOutcome.Ok : ClienteDeleteOutcome.NotFound;
    }

    // ---------- Helpers ----------

    private static async Task<bool> ItemExistsAsync(
        SqlConnection conn, string area, string id, CancellationToken ct, SqlTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM dbo.Pendiente WHERE Id = @id AND Area = @area";
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@area", area));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Mueve el <c>Actualizado</c> del pendiente: una nota nueva también es actividad.</summary>
    private static async Task TouchItemAsync(
        SqlConnection conn, SqlTransaction tx, string id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE dbo.Pendiente SET Actualizado = SYSUTCDATETIME() WHERE Id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddItemFields(SqlCommand cmd, PendienteWrite data)
    {
        cmd.Parameters.Add(new SqlParameter("@clienteNum", data.ClienteNum));
        cmd.Parameters.Add(new SqlParameter("@titulo", (object?)NullIfBlank(data.Titulo) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@descripcion", (object?)NullIfBlank(data.Descripcion) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@tipo", data.Tipo ?? PendientesDomain.TipoDefault));
        cmd.Parameters.Add(new SqlParameter("@prioridad", data.Prioridad ?? PendientesDomain.PrioridadDefault));
        cmd.Parameters.Add(new SqlParameter("@estado", data.Estado ?? PendientesDomain.EstadoDefault));
        cmd.Parameters.Add(new SqlParameter("@responsable", (object?)NullIfBlank(data.Responsable) ?? DBNull.Value));
    }

    private static void AddClienteFields(SqlCommand cmd, ClienteWrite data)
    {
        cmd.Parameters.Add(new SqlParameter("@cliente", data.Cliente.Trim()));
        cmd.Parameters.Add(new SqlParameter("@servicio", (object?)NullIfBlank(data.Servicio) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@categoria", (object?)NullIfBlank(data.Categoria) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@pais", (object?)NullIfBlank(data.Pais) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@coordinador", (object?)NullIfBlank(data.Coordinador) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@consultor", (object?)NullIfBlank(data.Consultor) ?? DBNull.Value));
    }

    private static PendienteCliente MapCliente(SqlDataReader reader) => new()
    {
        Num = reader.GetInt32(0),
        Cliente = reader.GetString(1),
        Servicio = ReadNullableString(reader, 2),
        Categoria = ReadNullableString(reader, 3),
        Pais = ReadNullableString(reader, 4),
        Coordinador = ReadNullableString(reader, 5),
        Consultor = ReadNullableString(reader, 6),
    };

    private static PendienteItem MapItem(SqlDataReader reader) => new()
    {
        Id = reader.GetString(0),
        ClienteNum = reader.GetInt32(1),
        Titulo = ReadNullableString(reader, 2),
        Descripcion = ReadNullableString(reader, 3),
        Tipo = ReadNullableString(reader, 4),
        Prioridad = ReadNullableString(reader, 5),
        Estado = ReadNullableString(reader, 6),
        Responsable = ReadNullableString(reader, 7),
        FechaCreacion = ReadDateOnly(reader, 8),
        Actualizado = reader.GetDateTime(9),
    };

    private static string? ReadNullableString(SqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static DateOnly? ReadDateOnly(SqlDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateOnly.FromDateTime(reader.GetDateTime(index));

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>2627 = violación de PK/unique constraint; 2601 = índice único.</summary>
    private static bool IsDuplicateKey(SqlException ex) =>
        ex.Number is 2627 or 2601;

    /// <summary>
    /// Id corto y único, con prefijo propio para distinguir lo creado en la plataforma de lo que
    /// genera la SWA. Cabe de sobra en el nvarchar(40) de la columna.
    /// </summary>
    private static string NewId()
    {
        var stamp = ToBase36(DateTime.UtcNow.Ticks);
        var salt = ToBase36(Random.Shared.Next(46_656)).PadLeft(3, '0'); // 36^3
        return IdPrefix + stamp + salt;
    }

    private static string ToBase36(long value)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value <= 0) return "0";
        var buffer = new Stack<char>();
        while (value > 0)
        {
            buffer.Push(alphabet[(int)(value % 36)]);
            value /= 36;
        }
        return new string([.. buffer]);
    }
}
