using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Clients;

namespace OptimizacionCostos.Api.Tests.Clients;

/// <summary>
/// El barrido de canónicas huérfanas de <c>PurgeCoreAsync</c>: la parte del borrado en cascada que
/// toca el catálogo de WAF, que es global y compartido entre todos los clientes.
///
/// <para><b>Por qué hace falta la base de verdad.</b> Lo que falla acá es el motor, no la lógica: el
/// catálogo tiene una FK autorreferente (<c>FK_waf_canonical_consolidates</c>) y SQL Server rechaza
/// borrar una fila que otra apunta, incluso cuando las dos entran en el mismo DELETE. Ningún doble en
/// memoria reproduce eso: el doble borra lo que se le pida. El 547 además se lleva la transacción
/// entera, así que una sola fila conflictiva dejaba el borrado de CUALQUIER cliente inservible. Es el
/// estado en que estaba la base de producción el 2026-08-18: tres huérfanas apuntadas por otras filas y
/// un cliente de prueba colgado en el selector porque su limpieza nunca llegó a correr.</para>
///
/// <para>El otro invariante que se cubre acá es el alcance. El barrido llegó a borrar las huérfanas de
/// toda la base, no solo las del cliente que se estaba eliminando, y el catálogo está curado a mano
/// (textos en español, revisión IA, matriz histórica). Que una eliminación se lleve contenido curado
/// ajeno es tan defecto como el 547, y no lo avisa nadie: simplemente aparece faltando.</para>
///
/// <para>Mismo gate que el resto de los tests de base: no-op sin <c>BIT_INTEGRATION_DB=1</c>.</para>
/// </summary>
public class WafCanonicalPurgeDbTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("BIT_INTEGRATION_DB") == "1";

    private static ISqlConnectionFactory NewFactory() =>
        new SqlConnectionFactory(
            AppConfig.FromConfiguration(new ConfigurationBuilder().Build()),
            NullLogger<SqlConnectionFactory>.Instance);

    [Fact]
    public async Task La_cascada_completa_con_una_huerfana_apuntada_por_otra()
    {
        if (!Enabled) return; // no-op fuera de la corrida de integración

        var factory = NewFactory();
        var clients = new SqlClientStore(factory);

        var tag = $"e2e-purge-canon-{Guid.NewGuid():N}";
        // El testigo sobrevive al borrado. Sirve para dos cosas: prestarle una recomendación VIVA al
        // catálogo, y comprobar que lo suyo no se va con el cliente ajeno.
        var testigoId = await clients.CreateAsync($"E2E purge canonica testigo {tag}", null, null, null, null);
        var victimaId = await clients.CreateAsync($"E2E purge canonica victima {tag}", null, null, null, null);

        try
        {
            // --- Lo que el cliente borrado usaba, y por lo tanto entra al barrido.

            // apuntada la usa la víctima Y la apunta apuntadora: es el caso exacto que tiraba el 547.
            var apuntada = await InsertCanonicalAsync(factory, tag, "apuntada", null);
            var apuntadora = await InsertCanonicalAsync(factory, tag, "apuntadora", apuntada);
            await InsertRecommendationAsync(factory, victimaId, apuntada, tag);
            await InsertRecommendationAsync(factory, victimaId, apuntadora, tag);

            // apuntadaPorViva queda huérfana con el borrado, pero la apunta dupViva, que sigue viva
            // porque su recomendación es del testigo.
            var apuntadaPorViva = await InsertCanonicalAsync(factory, tag, "apuntada-por-viva", null);
            var dupViva = await InsertCanonicalAsync(factory, tag, "dup-viva", apuntadaPorViva);
            await InsertRecommendationAsync(factory, victimaId, apuntadaPorViva, tag);
            await InsertRecommendationAsync(factory, testigoId, dupViva, tag);

            // libre la usa solo la víctima y nadie la apunta: es la que el barrido sí tiene que
            // borrar. Sin ella el test pasaría con un barrido que no barre nada.
            var libre = await InsertCanonicalAsync(factory, tag, "libre", null);
            await InsertRecommendationAsync(factory, victimaId, libre, tag);

            // --- Lo ajeno al cliente borrado, que el barrido no tiene que tocar.

            // delTestigo tiene recomendación de otro cliente: sigue en uso.
            var delTestigo = await InsertCanonicalAsync(factory, tag, "del-testigo", null);
            await InsertRecommendationAsync(factory, testigoId, delTestigo, tag);
            // ajenaHuerfana no la usa nadie y nunca la usó la víctima. Es el retrato de las 124 filas
            // curadas que había en la base: huérfanas de arrastre, ajenas a este borrado.
            var ajenaHuerfana = await InsertCanonicalAsync(factory, tag, "ajena-huerfana", null);

            // Antes del arreglo esta línea tiraba SqlException 547 por FK_waf_canonical_consolidates
            // y no borraba nada: ni la cascada ni el cliente.
            await clients.DeleteClientCascadeAsync(victimaId);

            // El cliente se fue de verdad, y el testigo no se lo llevó por delante.
            Assert.False(await ExisteCanonicalAsync(factory, victimaId, tabla: "clients"));
            Assert.True(await ExisteCanonicalAsync(factory, testigoId, tabla: "clients"));

            // Lo que el cliente usaba y nadie más referencia se va.
            Assert.False(await ExisteCanonicalAsync(factory, libre));
            Assert.False(await ExisteCanonicalAsync(factory, apuntadora));

            // Lo apuntado se conserva: es lo que evita el 547. apuntada queda para el barrido
            // siguiente, cuando apuntadora —que era quien la apuntaba— ya no exista.
            Assert.True(await ExisteCanonicalAsync(factory, apuntada));
            Assert.True(await ExisteCanonicalAsync(factory, apuntadaPorViva));
            Assert.True(await ExisteCanonicalAsync(factory, dupViva));

            // El puntero de consolidación de la fila viva sigue intacto: el arreglo conserva el grafo
            // de auditoría en vez de ponerlo en NULL para poder borrar.
            Assert.Equal(apuntadaPorViva, await ConsolidatesToAsync(factory, dupViva));

            // Y lo ajeno sigue ahí. ajenaHuerfana es la que se perdía con el barrido global.
            Assert.True(await ExisteCanonicalAsync(factory, delTestigo));
            Assert.True(await ExisteCanonicalAsync(factory, ajenaHuerfana));
        }
        finally
        {
            await LimpiarAsync(factory, tag, testigoId, victimaId);
        }
    }

    /// <summary>
    /// Guarda contra la próxima FK: si alguien agrega una tabla que apunte al catálogo y no la suma a
    /// las guardas del barrido, el borrado de clientes vuelve a caerse por 547 para todo el mundo.
    /// Lee las FKs de la base y no del esquema en código a propósito: tres de las que existen hoy en
    /// la base (comment, tracking e history) vienen del stack viejo y no están declaradas en
    /// <c>WafSchema.cs</c>, así que mirar el código dejaría la mitad afuera.
    /// </summary>
    [Fact]
    public async Task Las_guardas_cubren_toda_fk_que_apunte_al_catalogo()
    {
        if (!Enabled) return; // no-op fuera de la corrida de integración

        var sentencia = SentenciaDelBarrido();

        await using var conn = await NewFactory().OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT OBJECT_NAME(fk.parent_object_id) AS tabla_hija
              FROM sys.foreign_keys fk
             WHERE fk.referenced_object_id = OBJECT_ID('dbo.waf_recommendation_canonical')
            """;
        var hijas = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) hijas.Add(r.GetString(0));

        Assert.NotEmpty(hijas); // si esto falla, la consulta no está mirando nada

        // Con límite de palabra y no con Contains: `dbo.waf_recommendation_comment` satisface por
        // prefijo la búsqueda de `dbo.waf_recommendation`, así que un Contains dejaría pasar
        // justamente la falta de la guarda principal.
        var faltantes = hijas
            .Where(t => !Regex.IsMatch(sentencia, $@"\bdbo\.{Regex.Escape(t)}\b"))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(faltantes.Count == 0,
            "Estas tablas tienen FK a dbo.waf_recommendation_canonical y el barrido de huérfanas no " +
            "las mira, así que puede intentar borrar una fila que ellas referencian y tumbar con un " +
            "547 el borrado de cualquier cliente:\n  " + string.Join("\n  ", faltantes) +
            "\n\nAgregá un NOT EXISTS por cada una en la sentencia de waf_recommendation_canonical " +
            "de PurgeCoreAsync.");

        // La autorreferente se cubre por columna y no por tabla: el nombre de la tabla ya está ahí
        // porque es el objetivo del DELETE, así que sin esto la guarda pasaría de gratis.
        Assert.Contains("consolidates_to_id", sentencia, StringComparison.Ordinal);
        // Y el recorte por cliente, que es lo que evita que el barrido se lleve catálogo ajeno.
        Assert.Contains("#canon_del_cliente", sentencia, StringComparison.Ordinal);
    }

    /// <summary>Saca del código la sentencia SQL del barrido de canónicas, para contrastarla con las
    /// FKs reales de la base.</summary>
    private static string SentenciaDelBarrido([CallerFilePath] string archivoDeEstaPrueba = "")
    {
        // <raiz>/tests/OptimizacionCostos.Api.Tests/Clients/<este archivo>
        var raiz = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(archivoDeEstaPrueba)!, "..", "..", ".."));
        var store = Path.Combine(raiz, "src", "OptimizacionCostos.Api", "Features", "Clients", "SqlClientStore.cs");
        Assert.True(File.Exists(store),
            $"no se encontró '{store}'. Si la estructura del repo cambió, hay que ajustar esta prueba.");

        var texto = File.ReadAllText(store);
        var m = Regex.Match(texto, "\"waf_recommendation_canonical\",\\s*\"\"\"(.*?)\"\"\"", RegexOptions.Singleline);
        Assert.True(m.Success,
            "no se pudo aislar la sentencia del barrido en SqlClientStore.cs. Si se reescribió esa " +
            "llamada, hay que ajustar esta prueba.");
        return m.Groups[1].Value;
    }

    // ---- utilidades de armado y limpieza ----

    private static async Task<int> InsertCanonicalAsync(
        ISqlConnectionFactory factory, string tag, string sufijo, int? consolidatesTo)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.waf_recommendation_canonical
                (advisor_name, advisor_category, pillar_number, review_scope_es, benefit_es,
                 client_action_es, bit_action_es, consolidates_to_id)
            VALUES (@name, @cat, 1, '-', '-', '-', '-', @consolidates);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """;
        cmd.Parameters.Add(new SqlParameter("@name", $"{sufijo} {tag}"));
        cmd.Parameters.Add(new SqlParameter("@cat", $"E2E {tag}"));
        // null de C# en un SqlParameter revienta con 8178: va DBNull.Value.
        cmd.Parameters.Add(new SqlParameter("@consolidates", (object?)consolidatesTo ?? DBNull.Value));
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task InsertRecommendationAsync(
        ISqlConnectionFactory factory, int clientId, int canonicalId, string tag)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.waf_recommendation
                (client_id, canonical_id, matrix_code, business_impact, impact_number,
                 resource_count, first_seen_at, last_seen_at)
            VALUES (@client, @canonical, @code, 'Medium', 2, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        cmd.Parameters.Add(new SqlParameter("@client", clientId));
        cmd.Parameters.Add(new SqlParameter("@canonical", canonicalId));
        cmd.Parameters.Add(new SqlParameter("@code", $"E2E-{canonicalId}-{tag[^6..]}"));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>¿Sigue existiendo la fila? Sirve para el catálogo y para clients, que es la otra cosa
    /// que este test tiene que comprobar que se fue.</summary>
    private static async Task<bool> ExisteCanonicalAsync(
        ISqlConnectionFactory factory, int id, string tabla = "waf_recommendation_canonical")
    {
        var columna = tabla == "clients" ? "client_id" : "canonical_id";
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(1) FROM dbo.{tabla} WHERE {columna} = @p";
        cmd.Parameters.Add(new SqlParameter("@p", id));
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    private static async Task<int?> ConsolidatesToAsync(ISqlConnectionFactory factory, int canonicalId)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT consolidates_to_id FROM dbo.waf_recommendation_canonical WHERE canonical_id = @p";
        cmd.Parameters.Add(new SqlParameter("@p", canonicalId));
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? null : (int)v;
    }

    /// <summary>Limpia lo propio sin pasar por la cascada: por tag, así una corrida que se cayó a la
    /// mitad tampoco deja nada atrás. El orden es hijos a padres, y los punteros de consolidación se
    /// sueltan antes de borrar el catálogo para no chocar con la misma FK que motiva este test.</summary>
    private static async Task LimpiarAsync(ISqlConnectionFactory factory, string tag, params int[] clientIds)
    {
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DECLARE @canon TABLE (id INT);
            INSERT INTO @canon SELECT canonical_id FROM dbo.waf_recommendation_canonical WHERE advisor_category = @cat;
            DELETE FROM dbo.waf_resource_finding
             WHERE recommendation_id IN (
                SELECT recommendation_id FROM dbo.waf_recommendation WHERE canonical_id IN (SELECT id FROM @canon));
            DELETE FROM dbo.waf_recommendation_comment WHERE canonical_id IN (SELECT id FROM @canon);
            DELETE FROM dbo.waf_tracking_history WHERE canonical_id IN (SELECT id FROM @canon);
            DELETE FROM dbo.waf_recommendation_tracking WHERE canonical_id IN (SELECT id FROM @canon);
            DELETE FROM dbo.waf_recommendation WHERE canonical_id IN (SELECT id FROM @canon);
            DELETE FROM dbo.waf_canonical_alias WHERE canonical_id IN (SELECT id FROM @canon);
            UPDATE dbo.waf_recommendation_canonical SET consolidates_to_id = NULL
             WHERE canonical_id IN (SELECT id FROM @canon);
            DELETE FROM dbo.waf_recommendation_canonical WHERE canonical_id IN (SELECT id FROM @canon);
            DELETE FROM dbo.user_client_assignment WHERE client_id IN (SELECT value FROM STRING_SPLIT(@ids, ','));
            DELETE FROM dbo.clients WHERE client_id IN (SELECT value FROM STRING_SPLIT(@ids, ','));
            """;
        cmd.Parameters.Add(new SqlParameter("@cat", $"E2E {tag}"));
        cmd.Parameters.Add(new SqlParameter("@ids", string.Join(',', clientIds)));
        await cmd.ExecuteNonQueryAsync();
    }
}
