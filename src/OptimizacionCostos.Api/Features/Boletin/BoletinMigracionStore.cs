using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Boletin;

public sealed record MigracionEntry(
    int Id, string Clave, string Desde, string Hacia, string Notas,
    string MatchPattern, string? LearnMoreUrl, bool IsActive);

public interface IBoletinMigracionStore
{
    Task<IReadOnlyList<MigracionEntry>> ListAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<int> CreateAsync(IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task<bool> UpdateAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task<bool> SoftDeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>Whitelist anti-inyección: solo estas columnas son editables vía CRUD (patrón AlertColumns).</summary>
public static class MigracionColumns
{
    public static readonly string[] Editable =
        ["clave", "desde", "hacia", "notas", "match_pattern", "learn_more_url", "is_active"];
}

/// <summary>El UNIQUE(clave) de dbo.boletin_migracion cubre también las filas desactivadas: reactivar
/// una clave que el usuario desactivó por error (o cambió de opinión) es un caso legítimo de undelete,
/// pero crear una clave que ya está ACTIVA es un duplicado real. CreateAsync distingue ambos casos
/// antes de tocar la BD y lanza esta excepción solo para el segundo.</summary>
public sealed class MigracionClaveDuplicadaException(string clave)
    : Exception($"Ya existe una ruta activa con la clave '{clave}'.");

/// <summary>Catálogo GLOBAL de rutas de migración del Boletín.
/// Seed embebido idempotente por tabla-vacía (NO pisa ediciones del consultor, patrón AlertCatalogSchema).</summary>
public sealed class BoletinMigracionStore(ISqlConnectionFactory factory) : IBoletinMigracionStore
{
    private static object Db(object? v) => v ?? DBNull.Value;

    public async Task<IReadOnlyList<MigracionEntry>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await PrepareAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, clave, desde, hacia, notas, match_pattern, learn_more_url, is_active
            FROM dbo.boletin_migracion
            {(includeInactive ? "" : "WHERE is_active = 1")}
            ORDER BY desde, clave
            """;
        var list = new List<MigracionEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    /// <summary>Resultado puro (sin BD, testeable directo) de decidir qué hacer con una clave que
    /// llega en un CreateAsync, en función de si ya existe una fila con esa clave y su estado.</summary>
    public enum ClaveLookupOutcome { Insert, Undelete, Conflict }

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
                lookup.CommandText = "SELECT id, is_active FROM dbo.boletin_migracion WHERE clave = @clave";
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
                    throw new MigracionClaveDuplicadaException(clave);
                case ClaveLookupOutcome.Undelete:
                    // is_active se fuerza aparte (siempre 1 en un undelete); excluirlo de updCols
                    // evita asignarlo dos veces en el mismo SET.
                    var updCols = fields.Keys.Where(k => MigracionColumns.Editable.Contains(k) && k != "is_active").ToList();
                    await using (var update = conn.CreateCommand())
                    {
                        update.CommandText = $"""
                            UPDATE dbo.boletin_migracion SET {string.Join(", ", updCols.Select(c => $"{c} = @{c}"))},
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

        var cols = fields.Keys.Where(k => MigracionColumns.Editable.Contains(k)).ToList();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO dbo.boletin_migracion ({string.Join(", ", cols)})
            OUTPUT INSERTED.id VALUES ({string.Join(", ", cols.Select(c => "@" + c))})
            """;
        foreach (var c in cols) cmd.Parameters.Add(new SqlParameter("@" + c, Db(fields[c])));
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> UpdateAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        var cols = fields.Keys.Where(k => MigracionColumns.Editable.Contains(k)).ToList();
        if (cols.Count == 0) return false;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE dbo.boletin_migracion SET {string.Join(", ", cols.Select(c => $"{c} = @{c}"))},
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
        cmd.CommandText = "UPDATE dbo.boletin_migracion SET is_active = 0, updated_at = SYSUTCDATETIME() WHERE id = @id";
        cmd.Parameters.Add(new SqlParameter("@id", id));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static MigracionEntry Map(SqlDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.GetBoolean(7));

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
            IF OBJECT_ID('dbo.boletin_migracion', 'U') IS NULL
            CREATE TABLE dbo.boletin_migracion (
              id INT IDENTITY(1,1) PRIMARY KEY,
              clave NVARCHAR(64) NOT NULL,
              desde NVARCHAR(256) NOT NULL,
              hacia NVARCHAR(256) NOT NULL,
              notas NVARCHAR(MAX) NOT NULL,
              match_pattern NVARCHAR(256) NOT NULL,
              learn_more_url NVARCHAR(1024) NULL,
              is_active BIT NOT NULL DEFAULT 1,
              created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
              updated_at DATETIME2 NULL,
              CONSTRAINT UX_boletin_migracion_clave UNIQUE (clave))
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
            count.CommandText = "SELECT COUNT(1) FROM dbo.boletin_migracion";
            if ((int)(await count.ExecuteScalarAsync(ct))! > 0) { await tx.CommitAsync(ct); return; }
        }
        foreach (var e in ReadSeedEntries())
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO dbo.boletin_migracion
                  (clave, desde, hacia, notas, match_pattern, learn_more_url)
                VALUES (@clave, @desde, @hacia, @notas, @pattern, @url)
                """;
            cmd.Parameters.Add(new SqlParameter("@clave", e.Clave));
            cmd.Parameters.Add(new SqlParameter("@desde", e.Desde));
            cmd.Parameters.Add(new SqlParameter("@hacia", e.Hacia));
            cmd.Parameters.Add(new SqlParameter("@notas", e.Notas));
            cmd.Parameters.Add(new SqlParameter("@pattern", e.MatchPattern));
            cmd.Parameters.Add(new SqlParameter("@url", Db(e.LearnMoreUrl)));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Lee el seed embebido. Interno + testeable (el JSON es dato de negocio, no decoración).</summary>
    internal static IReadOnlyList<MigracionEntry> ReadSeedEntries()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("OptimizacionCostos.Api.Features.Boletin.Seed.migracion_seed.json")
            ?? throw new InvalidOperationException("migracion_seed.json no está embebido.");
        using var doc = JsonDocument.Parse(stream);
        var list = new List<MigracionEntry>();
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            list.Add(new MigracionEntry(
                0,
                e.GetProperty("clave").GetString()!,
                e.GetProperty("desde").GetString()!,
                e.GetProperty("hacia").GetString()!,
                e.GetProperty("notas").GetString()!,
                e.GetProperty("match_pattern").GetString()!,
                e.TryGetProperty("learn_more_url", out var u) ? u.GetString() : null,
                true));
        }
        return list;
    }
}
