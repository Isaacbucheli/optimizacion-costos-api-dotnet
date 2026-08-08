using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>
/// Trae las recomendaciones de la matriz WAF de un cliente, al grano recomendación × canónica ×
/// tracking (mismo universo que <c>BuildExportRowsAsync</c> de <c>WafController</c>, del que sale
/// este SQL). Recibe una <see cref="SqlConnection"/> ya abierta: no asegura el schema WAF ni
/// administra la conexión, eso es responsabilidad del ensamblador que lo llama junto a los demás
/// recolectores del informe.
///
/// <para><b>Los dos filtros de la pantalla de la matriz SÍ aplican acá, no son "parámetros de
/// pantalla".</b> <c>security_managed_externally</c> es una bandera por cliente en
/// <c>dbo.clients</c>, y la aplican las tres salidas del producto (pantalla WAF, export a Excel e
/// informe de gestión mensual): cuando el cliente gestiona su seguridad por fuera, las tres ocultan
/// el pilar de Seguridad entero. <see cref="Sql"/> replica exactamente eso
/// (<c>WafController.ListRecommendations</c>/<c>BuildExportRowsAsync</c>,
/// <c>ReportBuilder.WafRecommendationsAsync</c>): mismo <see cref="WafConstants.SecurityPillar"/>,
/// mismo criterio de "pilar entero afuera". El filtro por suscripción administrada tampoco es de
/// pantalla: sin él, una recomendación cuyos hallazgos viven todos en una suscripción que el
/// usuario dejó de administrar queda activa para siempre (<c>SqlWafIngestionStore</c> solo resuelve
/// hallazgos de las suscripciones que la corrida de ingesta escaneó).
/// </para>
///
/// <para><b>Excepción de la re-revisión (IMPORTANTE 1): los hallazgos cargados a mano quedan
/// EXENTOS del filtro de suscripciones administradas, no sujetos a él.</b> Un hallazgo con
/// <c>subscription_id = 'importado'</c> (<see cref="WafConstants.ManualSubscriptionId"/>, el que
/// escribe <c>ClosedXmlWafImporter.CreateManualFindingsAsync</c> al cargar el Excel histórico de la
/// matriz) no pertenece a ninguna suscripción real del cliente: no tiene sentido preguntarle si
/// está administrada, así que cuenta como si lo estuviera siempre. Sin esta excepción, una
/// recomendación cuyos hallazgos activos son 100% importados desaparecía de la matriz del informe
/// en cuanto el cliente tuviera alguna suscripción real administrada (el filtro exige que ALGÚN
/// hallazgo esté en esa lista, y 'importado' nunca puede estarlo), y en las recomendaciones mixtas
/// (parte real, parte importada) el conteo de recursos contaba de menos por el mismo motivo. Por
/// eso <see cref="Sql"/> ya NO reusa <see cref="WafSubscriptionFilter.ExistsPredicate"/>/
/// <see cref="WafSubscriptionFilter.ResourceCountExpr"/> tal cual (esos expulsarían 'importado', que
/// nunca puede estar en una lista de suscripciones reales): <see cref="ExistsPredicateConExcepcionManual"/>/
/// <see cref="ResourceCountExprConExcepcionManual"/> son variantes locales que aceptan
/// "administrada O importada" en vez de solo "administrada", reusando de
/// <see cref="WafSubscriptionFilter"/> solo lo genérico (<c>ParamNames</c>/<c>AddParameters</c>).
/// </para>
///
/// <para>Contraste a propósito con <see cref="AdvisorRecolector"/>, que SÍ excluye 'importado' del
/// todo (<c>f.subscription_id &lt;&gt; 'importado'</c>): ahí el hallazgo se publica fila por fila
/// con su propia suscripción y tipo de recurso en un desglose que agrupa por esos dos campos, y
/// dejarlo entrar mostraría "(matriz historica)" como si fuera una suscripción real del cliente, con
/// su propio porcentaje sobre el total. Acá en la matriz el hallazgo nunca se expone individual:
/// solo decide si la recomendación entra y cuánto suma el conteo de recursos, así que no hay ningún
/// campo ficticio que se filtre a otro lado. Mismo dato (<c>subscription_id = 'importado'</c>), dos
/// decisiones distintas, las dos correctas en su contexto: no unificarlas.</para>
/// </summary>
public static class MatrizRecolector
{
    /// <summary>
    /// SQL dinámico: los dos filtros de <see cref="LeerAsync"/> (seguridad gestionada externamente,
    /// suscripciones administradas) cambian el WHERE y la expresión de <c>resource_count</c> según
    /// el cliente, así que no puede ser una constante fija como antes.
    /// </summary>
    internal static string Sql(IReadOnlyList<string> suscripcionesAdministradas, bool seguridadGestionadaExternamente)
    {
        var secFilter = seguridadGestionadaExternamente ? $" AND c.pillar_number <> {WafConstants.SecurityPillar}" : "";
        var subFilter = ExistsPredicateConExcepcionManual("r", suscripcionesAdministradas);
        var resourceCountExpr = ResourceCountExprConExcepcionManual("r", suscripcionesAdministradas);
        return $"""
            SELECT
                r.canonical_id,
                r.matrix_code,
                c.pillar_number,
                c.review_scope_es,
                r.first_seen_at,
                r.impact_number,
                t.priority_override,
                t.projected_bit_effort,
                COALESCE(t.completion_pct, 0) AS completion_pct,
                t.execution_log,
                {resourceCountExpr} AS resource_count,
                c.is_excluded
            FROM dbo.waf_recommendation r
            INNER JOIN dbo.waf_recommendation_canonical c ON c.canonical_id = r.canonical_id
            LEFT JOIN dbo.waf_recommendation_tracking t
                ON t.client_id = r.client_id AND t.canonical_id = r.canonical_id
            WHERE r.client_id = @clientId
              AND r.is_active = 1
              AND COALESCE(r.is_dismissed, 0) = 0{secFilter}{subFilter}
            ORDER BY c.pillar_number, r.impact_number, c.review_scope_es
            """;
    }

    /// <summary>
    /// Como <see cref="WafSubscriptionFilter.ExistsPredicate"/>, pero con la excepción de la
    /// re-revisión (IMPORTANTE 1, ver el comentario de clase): un hallazgo con
    /// <c>subscription_id = 'importado'</c> cuenta como si estuviera en la lista de administradas,
    /// aunque nunca pueda estar literalmente en ella. El EXISTS pasa a exigir "hallazgo activo
    /// administrado O importado" en vez de solo "administrado".
    /// </summary>
    private static string ExistsPredicateConExcepcionManual(string recAlias, IReadOnlyList<string> administradas) =>
        $"""
        {"\n"}  AND EXISTS (
               SELECT 1 FROM dbo.waf_resource_finding wsf
               WHERE wsf.recommendation_id = {recAlias}.recommendation_id
                 AND wsf.status = 'active'
                 AND (wsf.subscription_id IN ({WafSubscriptionFilter.ParamNames(administradas)})
                      OR wsf.subscription_id = '{WafConstants.ManualSubscriptionId}'))
        """;

    /// <summary>Misma excepción que <see cref="ExistsPredicateConExcepcionManual"/>, para que el
    /// conteo de recursos de una recomendación mixta (parte real, parte importada) no cuente de
    /// menos: un hallazgo importado suma igual que uno en una suscripción administrada.</summary>
    private static string ResourceCountExprConExcepcionManual(string recAlias, IReadOnlyList<string> administradas) =>
        $"""
        (SELECT COUNT(*) FROM dbo.waf_resource_finding wsc
         WHERE wsc.recommendation_id = {recAlias}.recommendation_id
           AND wsc.status = 'active'
           AND (wsc.subscription_id IN ({WafSubscriptionFilter.ParamNames(administradas)})
                OR wsc.subscription_id = '{WafConstants.ManualSubscriptionId}'))
        """;

    /// <param name="suscripcionesAdministradas">Ids de <c>client_azure_subscriptions</c> activas y
    /// administradas del cliente (ver <c>SqlInsumosBdRecolector.SuscripcionesAdministradasAsync</c>).
    /// No es la única copia de este predicado en el módulo: <see cref="RetirosRecolector"/> mantiene
    /// la suya porque la forma de su consulta es distinta (EXISTS correlacionado contra
    /// <c>boletin_retirement.subscription_id</c>, no un SELECT plano) — mismas columnas y
    /// condiciones, SQL escrito dos veces a propósito, no una desprolijidad. Vacía → sin nada que
    /// reportar: se devuelve sin consultar, porque <see cref="WafSubscriptionFilter"/> trata una
    /// lista vacía como "sin selección" (todo pasa), el significado opuesto al que necesita este
    /// filtro. Los hallazgos con <c>subscription_id = 'importado'</c> están exentos de este filtro
    /// (ver <see cref="ExistsPredicateConExcepcionManual"/> y el comentario de clase): no cuentan
    /// como administrados, pero tampoco quedan fuera de alcance por no estarlo.</param>
    /// <param name="seguridadGestionadaExternamente">Ver el comentario de clase.</param>
    public static async Task<IReadOnlyList<MatrizFila>> LeerAsync(
        SqlConnection conn, int clientId, IReadOnlyList<string> suscripcionesAdministradas,
        bool seguridadGestionadaExternamente, CancellationToken ct = default)
    {
        if (suscripcionesAdministradas.Count == 0) return [];

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql(suscripcionesAdministradas, seguridadGestionadaExternamente);
        cmd.Parameters.Add(new SqlParameter("@clientId", clientId));
        WafSubscriptionFilter.AddParameters(cmd, suscripcionesAdministradas);

        var items = new List<MatrizFila>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            items.Add(MapearFila(rd));
        return items;
    }

    internal static MatrizFila MapearFila(SqlDataReader r)
    {
        var pillarNumber = (int)r.GetByte(2);

        return new MatrizFila(
            CanonicalId: r.GetInt32(0),
            MatrixCode: r.IsDBNull(1) ? null : r.GetString(1),
            PillarNumber: pillarNumber,
            Ambito: AdvisorRecolector.EtiquetaPilar(pillarNumber),
            Hallazgo: r.GetString(3),
            Fecha: r.IsDBNull(4) ? null : DateOnly.FromDateTime(r.GetDateTime(4)),
            ImpactNumber: r.IsDBNull(5) ? null : r.GetByte(5),
            // priority_override crudo (1/2/3), sin la etiqueta "1 - ALTA" que arma el exportador de
            // Excel (ClosedXmlWafExporter.PriorityText): esa traducción es de la calculadora.
            Prioridad: r.IsDBNull(6) ? null : r.GetByte(6).ToString(),
            EsfuerzoTexto: r.IsDBNull(7) ? null : r.GetString(7),
            AvancePct: r.GetInt32(8),
            Registro: r.IsDBNull(9) ? null : r.GetString(9),
            ResourceCount: r.GetInt32(10),
            Excluida: r.GetBoolean(11));
    }
}
