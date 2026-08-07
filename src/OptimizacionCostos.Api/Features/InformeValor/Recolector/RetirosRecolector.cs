using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>
/// Trae los retiros de Azure vigentes de un cliente desde <c>boletin_retirement</c> (módulo
/// Boletín), agrupados por anuncio. No sale del cruce de Advisor de la matriz: ese cruce no trae
/// fecha de retiro, recurso concreto ni acción recomendada por recurso, que son justamente los
/// datos que la plantilla necesita para esta sección.
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
    /// Agrupado por <c>announcement_key</c>: un mismo anuncio puede traer varias filas, una por
    /// recurso/suscripción afectado. <c>title</c>/<c>recommended_action</c>/<c>retiring_feature</c>
    /// son funcionalmente iguales dentro de un mismo anuncio, pero SQL exige un agregado para
    /// columnas fuera del GROUP BY: <c>MAX</c> es solo "cualquiera de las filas del grupo", no un
    /// máximo con significado propio. Prioriza la traducción al español
    /// (<c>title_es</c>/<c>recommended_action_es</c>) y cae al original en inglés si todavía no se
    /// tradujo, para no dejar el campo vacío mientras la traducción está pendiente.
    /// </summary>
    internal const string Sql = """
        SELECT
            announcement_key,
            MAX(retiring_feature) AS retiring_feature,
            MAX(retirement_date) AS retirement_date,
            COALESCE(MAX(title_es), MAX(title)) AS titulo,
            COALESCE(MAX(recommended_action_es), MAX(recommended_action)) AS accion_recomendada,
            COUNT(*) AS recursos_afectados
        FROM dbo.boletin_retirement
        WHERE client_id = @clientId AND status = 'vigente'
        GROUP BY announcement_key
        ORDER BY retirement_date, announcement_key
        """;

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
