using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Boletin;

public sealed record LifecycleEntry(
    int Id, string Clave, string Producto, string Categoria,
    string MatchField, string MatchPattern,
    DateOnly EndOfSupport, string Recomendacion, string? LearnMoreUrl, bool IsActive);

public interface IBoletinLifecycleStore
{
    Task<IReadOnlyList<LifecycleEntry>> ListAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<LifecycleEntry?> GetAsync(int id, CancellationToken ct = default);
    Task<int> CreateAsync(IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task<bool> UpdateAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task<bool> SoftDeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>Whitelist anti-inyección: solo estas columnas son editables vía CRUD (patrón AlertColumns).</summary>
public static class LifecycleColumns
{
    public static readonly string[] Editable =
        ["clave", "producto", "categoria", "match_field", "match_pattern", "end_of_support", "recomendacion", "learn_more_url", "is_active"];
}

/// <summary>El UNIQUE(clave) de dbo.boletin_lifecycle cubre también las filas desactivadas: reactivar
/// una clave que el usuario desactivó por error (o cambió de opinión) es un caso legítimo de undelete,
/// pero crear una clave que ya está ACTIVA es un duplicado real. CreateAsync distingue ambos casos
/// antes de tocar la BD y lanza esta excepción solo para el segundo.</summary>
public sealed class LifecycleClaveDuplicadaException(string clave)
    : Exception($"Ya existe una entrada activa con la clave '{clave}'.");

/// <summary>Catálogo GLOBAL de lifecycle (fin de soporte de SO y motores de BD) del Boletín.
/// Seed embebido idempotente por tabla-vacía (NO pisa ediciones del consultor, patrón AlertCatalogSchema).</summary>
public sealed class BoletinLifecycleStore(ISqlConnectionFactory factory) : IBoletinLifecycleStore
{
    private static object Db(object? v) => v ?? DBNull.Value;

    public async Task<IReadOnlyList<LifecycleEntry>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, clave, producto, categoria, match_field, match_pattern,
                   end_of_support, recomendacion, learn_more_url, is_active
            FROM dbo.boletin_lifecycle
            {(includeInactive ? "" : "WHERE is_active = 1")}
            ORDER BY end_of_support, producto
            """;
        var list = new List<LifecycleEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task<LifecycleEntry?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, clave, producto, categoria, match_field, match_pattern,
                   end_of_support, recomendacion, learn_more_url, is_active
            FROM dbo.boletin_lifecycle WHERE id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    /// <summary>Resultado puro (sin BD, testeable directo) de decidir qué hacer con una clave que
    /// llega en un CreateAsync, en función de si ya existe una fila con esa clave y su estado.</summary>
    internal enum ClaveLookupOutcome { Insert, Undelete, Conflict }

    internal static ClaveLookupOutcome DecideCreateOutcome(bool claveExists, bool existingIsActive) =>
        !claveExists ? ClaveLookupOutcome.Insert
        : existingIsActive ? ClaveLookupOutcome.Conflict
        : ClaveLookupOutcome.Undelete;

    public async Task<int> CreateAsync(IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        // Desactivar (SoftDeleteAsync) es el flujo normal del producto y arrepentirse debe ser
        // posible desde el mismo form: si la clave que llega ya existe pero DESACTIVADA, la
        // resucitamos (undelete) con los datos nuevos en vez de reventar con SqlException 2627
        // por el UNIQUE(clave), que también cubre las filas inactivas.
        if (fields.TryGetValue("clave", out var claveObj) && claveObj is string clave && !string.IsNullOrEmpty(clave))
        {
            int? existingId = null;
            var existingIsActive = false;
            await using (var lookup = conn.CreateCommand())
            {
                lookup.CommandText = "SELECT id, is_active FROM dbo.boletin_lifecycle WHERE clave = @clave";
                lookup.Parameters.Add(new SqlParameter("@clave", clave));
                await using var r = await lookup.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    existingId = r.GetInt32(0);
                    existingIsActive = r.GetBoolean(1);
                }
            }

            switch (DecideCreateOutcome(existingId.HasValue, existingIsActive))
            {
                case ClaveLookupOutcome.Conflict:
                    throw new LifecycleClaveDuplicadaException(clave);
                case ClaveLookupOutcome.Undelete:
                    // is_active se fuerza aparte (siempre 1 en un undelete); excluirlo de updCols
                    // evita asignarlo dos veces en el mismo SET.
                    var updCols = fields.Keys.Where(k => LifecycleColumns.Editable.Contains(k) && k != "is_active").ToList();
                    await using (var update = conn.CreateCommand())
                    {
                        update.CommandText = $"""
                            UPDATE dbo.boletin_lifecycle SET {string.Join(", ", updCols.Select(c => $"{c} = @{c}"))},
                                   is_active = 1, updated_at = SYSUTCDATETIME()
                            WHERE id = @id
                            """;
                        update.Parameters.Add(new SqlParameter("@id", existingId!.Value));
                        foreach (var c in updCols) update.Parameters.Add(new SqlParameter("@" + c, Db(fields[c])));
                        await update.ExecuteNonQueryAsync(ct);
                    }
                    return existingId!.Value;
            }
        }

        var cols = fields.Keys.Where(k => LifecycleColumns.Editable.Contains(k)).ToList();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO dbo.boletin_lifecycle ({string.Join(", ", cols)})
            OUTPUT INSERTED.id VALUES ({string.Join(", ", cols.Select(c => "@" + c))})
            """;
        foreach (var c in cols) cmd.Parameters.Add(new SqlParameter("@" + c, Db(fields[c])));
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> UpdateAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        var cols = fields.Keys.Where(k => LifecycleColumns.Editable.Contains(k)).ToList();
        if (cols.Count == 0) return false;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE dbo.boletin_lifecycle SET {string.Join(", ", cols.Select(c => $"{c} = @{c}"))},
                   updated_at = SYSUTCDATETIME()
            WHERE id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", id));
        foreach (var c in cols) cmd.Parameters.Add(new SqlParameter("@" + c, Db(fields[c])));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> SoftDeleteAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dbo.boletin_lifecycle SET is_active = 0, updated_at = SYSUTCDATETIME() WHERE id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", id));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static LifecycleEntry Map(SqlDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        DateOnly.FromDateTime(r.GetDateTime(6)), r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.GetBoolean(9));

    /// <summary>Schema + seed perezoso (solo en lecturas, patrón SqlAlertCatalogStore.PrepareAsync).</summary>
    internal static async Task PrepareAsync(SqlConnection conn, CancellationToken ct)
    {
        await EnsureSchemaAsync(conn, ct);
        await SeedIfEmptyAsync(conn, ct);
    }

    internal static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.boletin_lifecycle', 'U') IS NULL
            CREATE TABLE dbo.boletin_lifecycle (
              id INT IDENTITY(1,1) PRIMARY KEY,
              clave NVARCHAR(64) NOT NULL,
              producto NVARCHAR(200) NOT NULL,
              categoria NVARCHAR(20) NOT NULL,
              match_field NVARCHAR(32) NOT NULL,
              match_pattern NVARCHAR(120) NOT NULL,
              end_of_support DATE NOT NULL,
              recomendacion NVARCHAR(MAX) NOT NULL,
              learn_more_url NVARCHAR(1024) NULL,
              is_active BIT NOT NULL DEFAULT 1,
              created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
              updated_at DATETIME2 NULL,
              CONSTRAINT UX_boletin_lifecycle_clave UNIQUE (clave))
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Todo-o-nada: el COUNT y TODOS los INSERTs del seed corren en UNA sola transacción.
    /// Sin esto, un fallo a mitad del loop (ej. conexión caída tras el INSERT #10 de N) deja el
    /// catálogo con COUNT&gt;0 pero INCOMPLETO — el guard de arriba nunca lo vuelve a sembrar (no
    /// está vacío), y el sync del Boletín lo trata como si fuera el catálogo completo: las entradas
    /// que faltaron simplemente nunca matchean contra el inventario, sin error ni aviso visible
    /// (under-match silencioso). Un catálogo parcial es peor que uno vacío: uno vacío al menos se
    /// reintenta en la próxima lectura.</summary>
    private static async Task SeedIfEmptyAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (var count = conn.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(1) FROM dbo.boletin_lifecycle";
            if ((int)(await count.ExecuteScalarAsync(ct))! > 0) { await tx.CommitAsync(ct); return; }
        }
        foreach (var e in ReadSeedEntries())
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO dbo.boletin_lifecycle
                  (clave, producto, categoria, match_field, match_pattern, end_of_support, recomendacion, learn_more_url)
                VALUES (@clave, @producto, @categoria, @field, @pattern, @eos, @reco, @url)
                """;
            cmd.Parameters.Add(new SqlParameter("@clave", e.Clave));
            cmd.Parameters.Add(new SqlParameter("@producto", e.Producto));
            cmd.Parameters.Add(new SqlParameter("@categoria", e.Categoria));
            cmd.Parameters.Add(new SqlParameter("@field", e.MatchField));
            cmd.Parameters.Add(new SqlParameter("@pattern", e.MatchPattern));
            cmd.Parameters.Add(new SqlParameter("@eos", e.EndOfSupport.ToDateTime(TimeOnly.MinValue)));
            cmd.Parameters.Add(new SqlParameter("@reco", e.Recomendacion));
            cmd.Parameters.Add(new SqlParameter("@url", Db(e.LearnMoreUrl)));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Lee el seed embebido. Interno + testeable (el JSON es dato de negocio, no decoración).</summary>
    internal static IReadOnlyList<LifecycleEntry> ReadSeedEntries()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("OptimizacionCostos.Api.Features.Boletin.Seed.lifecycle_seed.json")
            ?? throw new InvalidOperationException("lifecycle_seed.json no está embebido.");
        using var doc = JsonDocument.Parse(stream);
        var list = new List<LifecycleEntry>();
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            list.Add(new LifecycleEntry(
                0,
                e.GetProperty("clave").GetString()!,
                e.GetProperty("producto").GetString()!,
                e.GetProperty("categoria").GetString()!,
                e.GetProperty("match_field").GetString()!,
                e.GetProperty("match_pattern").GetString()!,
                DateOnly.Parse(e.GetProperty("end_of_support").GetString()!),
                e.GetProperty("recomendacion").GetString()!,
                e.TryGetProperty("learn_more_url", out var u) ? u.GetString() : null,
                true));
        }
        return list;
    }
}
