using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Boletin;

public sealed record NovedadRow(
    int Id, string FeedGuid, string Titulo, string? TituloEs, string Descripcion, string? DescripcionEs,
    string Link, string EstadoFeed, string CategoriaBit, string CategoriasFeedJson,
    DateTime PublishedAtUtc, bool IsActive);

public interface IBoletinNovedadStore
{
    /// <summary>Descarga el RSS, dedupe por feed_guid, inserta nuevas y traduce pendientes
    /// (best-effort, IA apagada = quedan en EN). Devuelve (Nuevas, Traducidas).</summary>
    Task<(int Nuevas, int Traducidas)> IngestAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NovedadRow>> ListAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<bool> UpdateAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
}

/// <summary>Whitelist anti-inyección del PUT: la ingesta NUNCA pisa lo que el consultor curó
/// (titulo_es/descripcion_es), así que esas columnas no son editables por API — solo la
/// clasificación BIT y el flag de visibilidad (patrón LifecycleColumns).</summary>
public static class NovedadColumns
{
    public static readonly string[] Editable = ["categoria_bit", "is_active"];

    /// <summary>Los 4 valores que hoy produce AzureUpdatesFeed.MapCategoriaBit (3 categorías
    /// mapeadas + el default "resiliencia_plataforma"). El PUT valida contra esta misma lista para
    /// que un consultor no pueda dejar una novedad en una categoría BIT que el front no reconoce.</summary>
    public static readonly string[] CategoriasBitValidas =
        ["productividad_ia", "seguridad_identidad", "costo_operacion", "resiliencia_plataforma"];
}

/// <summary>Ingesta GLOBAL de novedades del feed de Azure Updates (no por cliente): a diferencia del
/// catálogo de lifecycle, esta tabla no tiene seed — nace vacía y <see cref="IngestAsync"/> es la
/// única fuente de filas. Dedupe puro por feed_guid: una vez insertada, una fila NUNCA se vuelve a
/// tocar por la ingesta (ni siquiera si el feed cambió el texto), para no pisar titulo_es/descripcion_es
/// ni categoria_bit que el consultor haya curado a mano vía PUT.</summary>
public sealed class BoletinNovedadStore(
    ISqlConnectionFactory factory, IHttpClientFactory httpFactory,
    IBoletinTranslationService translation, ILogger<BoletinNovedadStore> logger) : IBoletinNovedadStore
{
    private static object Db(object? v) => v ?? DBNull.Value;

    public async Task<(int Nuevas, int Traducidas)> IngestAsync(CancellationToken ct = default)
    {
        // Timeout explícito de 60s (el feed de Microsoft puede tardar): se fija en el cliente, NO en
        // el registro DI, porque este HttpClient es genérico (IHttpClientFactory "simple", sin tipo).
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);

        // Deja que XmlException/HttpRequestException/TaskCanceledException (timeout) suban tal cual:
        // el controller las traduce a 502 controlado. Acá NO se atrapan porque son fallos duros del
        // feed (a diferencia de la traducción, que es best-effort y sí se atrapa más abajo).
        var rssXml = await http.GetStringAsync(AzureUpdatesFeed.RssUrl, ct);
        var items = AzureUpdatesFeed.Parse(rssXml);

        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        var nuevas = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            // Guardas defensivas contra cambios de formato del feed (review E3-T2): un guid más
            // largo que la columna señala un formato distinto al validado (guids numéricos cortos)
            // y se SALTA con warning; títulos/links largos se truncan para que UNA fila anómala
            // jamás tumbe el lote entero con un SqlException de truncado (500 crudo).
            if (item.FeedGuid.Length > 32)
            {
                logger.LogWarning("ingesta de novedades: guid inesperado de {Len} chars, item saltado ({Titulo})",
                    item.FeedGuid.Length, item.Titulo.Length > 60 ? item.Titulo[..60] : item.Titulo);
                continue;
            }
            var titulo = item.Titulo.Length > 512 ? item.Titulo[..512] : item.Titulo;
            var link = item.Link.Length > 1024 ? item.Link[..1024] : item.Link;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                IF NOT EXISTS (SELECT 1 FROM dbo.boletin_novedad WHERE feed_guid = @guid)
                INSERT INTO dbo.boletin_novedad
                  (feed_guid, titulo, descripcion, link, estado_feed, categoria_bit, categorias_feed, published_at)
                VALUES (@guid, @titulo, @descripcion, @link, @estado, @categoria, @categoriasJson, @published)
                """;
            cmd.Parameters.Add(new SqlParameter("@guid", item.FeedGuid));
            cmd.Parameters.Add(new SqlParameter("@titulo", titulo));
            cmd.Parameters.Add(new SqlParameter("@descripcion", item.Descripcion));
            cmd.Parameters.Add(new SqlParameter("@link", link));
            cmd.Parameters.Add(new SqlParameter("@estado", item.EstadoFeed));
            cmd.Parameters.Add(new SqlParameter("@categoria", item.CategoriaBit));
            cmd.Parameters.Add(new SqlParameter("@categoriasJson", JsonSerializer.Serialize(item.CategoriasFeed)));
            // datetime2 explícito: la inferencia por defecto (datetime, ticks de 1/300s) redondea
            // distinto que la columna datetime2 de la tabla (bug ya conocido en el proyecto, ver
            // SqlWafIngestionStore.Param) — acá no se compara published_at por igualdad, pero insertar
            // con el tipo correcto evita arrastrar el mismo problema si algún día se necesita.
            cmd.Parameters.Add(new SqlParameter("@published", SqlDbType.DateTime2) { Value = item.PublishedAtUtc });
            try
            {
                var affected = await cmd.ExecuteNonQueryAsync(ct);
                if (affected > 0) nuevas++;
            }
            catch (SqlException ex) when (ex.Number == 2627)
            {
                // Carrera de doble ingesta simultánea: el IF NOT EXISTS no es atómico y el UNIQUE
                // de feed_guid atrapa al segundo INSERT. Es un duplicado legítimo, no un error.
                logger.LogInformation("ingesta de novedades: guid {Guid} insertado por otra ingesta concurrente", item.FeedGuid);
            }
        }

        // Traducción fiel es (best-effort, patrón BoletinService.TranslatePendingAsync/E1): IA no
        // configurada => se omite en silencio (0 traducidas, el front cae al EN); configurada y
        // fallando => se loguea pero NO revienta la ingesta (las filas nuevas ya están confirmadas).
        var traducidas = 0;
        if (translation.IsConfigured)
        {
            try { traducidas = await TranslatePendingAsync(conn, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ingesta de novedades: la traduccion fallo, se conservan en EN");
            }
        }

        return (nuevas, traducidas);
    }

    public async Task<IReadOnlyList<NovedadRow>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, feed_guid, titulo, titulo_es, descripcion, descripcion_es, link, estado_feed,
                   categoria_bit, categorias_feed, published_at, is_active
            FROM dbo.boletin_novedad
            {(includeInactive ? "" : "WHERE is_active = 1")}
            ORDER BY published_at DESC
            """;
        var list = new List<NovedadRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task<bool> UpdateAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        var cols = fields.Keys.Where(k => NovedadColumns.Editable.Contains(k)).ToList();
        if (cols.Count == 0) return false;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE dbo.boletin_novedad SET {string.Join(", ", cols.Select(c => $"{c} = @{c}"))},
                   updated_at = SYSUTCDATETIME()
            WHERE id = @id
            """;
        cmd.Parameters.Add(new SqlParameter("@id", id));
        foreach (var c in cols) cmd.Parameters.Add(new SqlParameter("@" + c, Db(fields[c])));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Traduce titulo/descripcion pendientes de filas activas (espejo GLOBAL de
    /// BoletinService.TranslatePendingAsync: DISTINCT por texto, UPDATE con {col}_es IS NULL para
    /// jamás pisar una traducción ya curada, truncado 512 para titulo_es). Devuelve cuántas filas
    /// obtuvieron titulo_es en esta corrida — proxy simple y estable de "novedades traducidas"
    /// (descripcion_es se traduce igual pero no se cuenta aparte para no duplicar el conteo por fila).</summary>
    private async Task<int> TranslatePendingAsync(SqlConnection conn, CancellationToken ct)
    {
        var tituloTraducidas = 0;
        foreach (var (column, columnEs, maxLen) in new[] { ("titulo", "titulo_es", 512), ("descripcion", "descripcion_es", 0) })
        {
            var pending = new List<string>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT DISTINCT {column} FROM dbo.boletin_novedad
                    WHERE is_active = 1 AND {columnEs} IS NULL AND ISNULL({column}, N'') <> N''
                    """;
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) pending.Add(r.GetString(0));
            }
            if (pending.Count == 0) continue;

            var translated = await translation.TranslateToSpanishAsync(
                pending.Select((t, i) => new BoletinTranslationItem(i.ToString(), t)).ToList(), ct);

            for (var i = 0; i < pending.Count; i++)
            {
                var es = translated[i].Text;
                if (maxLen > 0 && es.Length > maxLen) es = es[..maxLen];
                await using var upd = conn.CreateCommand();
                upd.CommandText = $"""
                    UPDATE dbo.boletin_novedad SET {columnEs} = @es
                    WHERE {column} = @en AND {columnEs} IS NULL
                    """;
                upd.Parameters.Add(new SqlParameter("@en", pending[i]));
                upd.Parameters.Add(new SqlParameter("@es", es));
                var affected = await upd.ExecuteNonQueryAsync(ct);
                if (column == "titulo") tituloTraducidas += affected;
            }
        }
        return tituloTraducidas;
    }

    private static NovedadRow Map(SqlDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7),
        r.GetString(8), r.GetString(9), DateTime.SpecifyKind(r.GetDateTime(10), DateTimeKind.Utc), r.GetBoolean(11));

    internal static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.boletin_novedad','U') IS NULL
            CREATE TABLE dbo.boletin_novedad (
              id INT IDENTITY(1,1) PRIMARY KEY,
              feed_guid NVARCHAR(32) NOT NULL,
              titulo NVARCHAR(512) NOT NULL,
              titulo_es NVARCHAR(512) NULL,
              descripcion NVARCHAR(MAX) NOT NULL,
              descripcion_es NVARCHAR(MAX) NULL,
              link NVARCHAR(1024) NOT NULL,
              estado_feed NVARCHAR(20) NOT NULL,
              categoria_bit NVARCHAR(32) NOT NULL,
              categorias_feed NVARCHAR(MAX) NOT NULL,
              published_at DATETIME2 NOT NULL,
              is_active BIT NOT NULL DEFAULT 1,
              created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
              updated_at DATETIME2 NULL,
              CONSTRAINT UX_boletin_novedad_guid UNIQUE (feed_guid))
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
