using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>
/// Trae los retiros de Azure vigentes de un cliente desde <c>boletin_retirement</c> (módulo
/// Boletín), agrupados por anuncio. No sale del cruce de Advisor de la matriz: ese cruce no trae
/// fecha de retiro, recurso concreto ni acción recomendada por recurso, que son justamente los
/// datos que la plantilla necesita para esta sección.
///
/// <para><b>Tres reglas replicadas de <see cref="Boletin.BoletinAggregator"/>, a mano:</b> este
/// recolector lee <c>boletin_retirement</c> crudo con su propio SQL agregado, no pasa por
/// <see cref="Boletin.BoletinAggregator.BuildView"/>, así que ninguna de las tres sale gratis.
/// <c>recursos_afectados</c> cuenta solo filas con <c>azure_resource_id</c> no nulo, igual que el
/// KPI <c>resources</c> de <see cref="Boletin.BoletinAggregator.BuildView"/>: Service Health suele
/// guardar una fila por suscripción SIN recurso cuando Microsoft no publica los recursos
/// impactados (el caso común en este proyecto), y un <c>COUNT(*)</c> simple las cuenta como
/// recurso, mostrando más recursos afectados de los que el Boletín muestra para el mismo cliente.
/// <c>source = 'eol'</c> (fin de soporte) queda afuera del WHERE: el Boletín lo separa de los
/// retiros y lo cuenta como categoría propia (<c>eol_products</c>/<c>eol_resources</c>); mezclarlo
/// acá le atribuiría a "retiros" filas que el Boletín nunca cuenta ahí. Y solo entran suscripciones
/// administradas, mismo predicado que <c>BoletinService.ManagedSubscriptionsAsync</c>
/// (<c>client_azure_subscriptions.is_active</c> + <c>COALESCE(is_managed,1)</c> +
/// <c>client_azure_credentials.is_active</c>) — equivalente en SQL a lo que
/// <see cref="Boletin.BoletinAggregator.FilterToManaged"/> hace en memoria para la vista del
/// Boletín: sin este filtro, una fila histórica de una suscripción que el usuario dejó de
/// administrar sigue apareciendo en el informe aunque ya no aparezca en el Boletín.</para>
///
/// <para><b>Consecuencia de permisos a dejar escrita:</b> el módulo Boletín tiene su propia clave
/// de acceso. Con la decisión ya tomada de que el informe exige solo su propia clave (no la de
/// Boletín), un consultor con el informe habilitado y Boletín denegado va a poder ver retiros en
/// el informe igual. Es la decisión vigente, no un descuido de este recolector.</para>
///
/// <para>No asegura el schema de Boletín ni administra la conexión: eso es responsabilidad del
/// ensamblador que lo llama junto a los demás recolectores del informe.</para>
/// </summary>
public static class RetirosRecolector
{
    /// <summary>
    /// Agrupado por (<c>source</c>, <c>announcement_key</c>) — igual que
    /// <see cref="Boletin.BoletinAggregator.BuildView"/> agrupa sus filas — aunque hoy la clave no
    /// colisiona entre orígenes: un mismo anuncio puede traer varias filas, una por
    /// recurso/suscripción afectado. <c>title</c>/<c>recommended_action</c>/<c>retiring_feature</c>
    /// son funcionalmente iguales dentro de un mismo anuncio, pero SQL exige un agregado para
    /// columnas fuera del GROUP BY: <c>MAX</c> es solo "cualquiera de las filas del grupo", no un
    /// máximo con significado propio. Prioriza la traducción al español
    /// (<c>title_es</c>/<c>recommended_action_es</c>) y cae al original en inglés si todavía no se
    /// tradujo, para no dejar el campo vacío mientras la traducción está pendiente. Los filtros de
    /// origen y de suscripciones administradas, y el conteo de <c>recursos_afectados</c>, se
    /// explican en el comentario de la clase.
    /// </summary>
    internal const string Sql = """
        SELECT
            b.announcement_key,
            MAX(b.retiring_feature) AS retiring_feature,
            MAX(b.retirement_date) AS retirement_date,
            COALESCE(MAX(b.title_es), MAX(b.title)) AS titulo,
            COALESCE(MAX(b.recommended_action_es), MAX(b.recommended_action)) AS accion_recomendada,
            COUNT(CASE WHEN b.azure_resource_id IS NOT NULL THEN 1 END) AS recursos_afectados
        FROM dbo.boletin_retirement b
        WHERE b.client_id = @clientId AND b.status = 'vigente' AND b.source <> 'eol'
          AND EXISTS (
              SELECT 1 FROM dbo.client_azure_subscriptions s
              INNER JOIN dbo.client_azure_credentials c ON s.credential_id = c.credential_id
              WHERE s.client_id = @clientId AND s.subscription_id = b.subscription_id
                AND s.is_active = 1 AND COALESCE(s.is_managed, 1) = 1 AND c.is_active = 1
          )
        GROUP BY b.source, b.announcement_key
        ORDER BY retirement_date, announcement_key
        """;

    /// <summary>
    /// Última corrida del sync del Boletín para ese cliente (<c>boletin_sync</c>, la misma tabla y el
    /// mismo <c>ORDER BY started_at DESC</c> que usa <c>BoletinService.LoadLastSyncAsync</c> para el
    /// panel del Boletín). Sin fila, el módulo nunca sincronizó a este cliente.
    ///
    /// <para><b>Por qué el informe la necesita.</b> Sin ella, "0 retiros" se ve exactamente igual
    /// cuando Azure no anunció nada sobre el parque y cuando nadie fue a buscarlo: la sincronización
    /// del Boletín es manual y por cliente, y el módulo nace denegado en permisos, así que "nunca
    /// corrió" no es un borde, es el estado inicial de todo cliente nuevo. El artefacto publicaba la
    /// tarjeta "0 retiros" con la prosa "el export no reporta características en proceso de retiro
    /// sobre este parque", que además nombra una fuente que no es la que se consultó.</para>
    /// </summary>
    internal const string SqlUltimaCorrida = """
        SELECT TOP 1 status, started_at, finished_at
        FROM dbo.boletin_sync WHERE client_id = @clientId ORDER BY started_at DESC
        """;

    /// <summary>Ver <see cref="SqlUltimaCorrida"/>. <c>null</c> = el Boletín nunca corrió para este
    /// cliente.</summary>
    public static async Task<CorridaBoletin?> LeerUltimaCorridaAsync(
        SqlConnection conn, int clientId, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SqlUltimaCorrida;
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return MapearCorrida(rd);
    }

    internal static CorridaBoletin MapearCorrida(SqlDataReader r) => new(
        Estado: r.GetString(0),
        IniciadaEn: r.GetDateTime(1),
        FinalizadaEn: r.IsDBNull(2) ? null : r.GetDateTime(2));

    public static async Task<IReadOnlyList<RetiroFila>> LeerAsync(
        SqlConnection conn, int clientId, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql;
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));

        var items = new List<RetiroFila>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            items.Add(MapearFila(rd));
        return items;
    }

    internal static RetiroFila MapearFila(SqlDataReader r) => new(
        AnnouncementKey: r.GetString(0),
        Caracteristica: r.IsDBNull(1) ? null : r.GetString(1),
        FechaRetiro: r.IsDBNull(2) ? null : DateOnly.FromDateTime(r.GetDateTime(2)),
        Titulo: r.IsDBNull(3) ? null : r.GetString(3),
        AccionRecomendada: r.IsDBNull(4) ? null : r.GetString(4),
        RecursosAfectados: r.GetInt32(5));
}
