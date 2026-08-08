using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// No tocan la base: verifican el texto del SQL expuesto (MatrizRecolector.Sql) y la forma del
/// record, mismo estilo que AdvisorRecolectorTests. Desde la revisión de rama de la Entrega 2a,
/// Sql es una función de la lista de suscripciones administradas y de la bandera de seguridad
/// gestionada externamente (antes una constante fija): los tests que solo miran forma/orden de
/// columnas usan una lista de un solo elemento, representativa de lo que manda el ensamblador en
/// producción (nunca lista vacía: <see cref="MatrizRecolector.LeerAsync"/> corta antes de consultar).
/// </summary>
public sealed class MatrizRecolectorTests
{
    private static readonly string[] UnaSuscripcion = ["sub-1"];

    [Fact]
    public void El_esfuerzo_se_devuelve_como_texto_sin_parsear()
    {
        var p = typeof(MatrizFila).GetProperty("EsfuerzoTexto");
        Assert.NotNull(p);
        Assert.Equal(typeof(string), Nullable.GetUnderlyingType(p!.PropertyType) ?? p.PropertyType);
    }

    /// <summary>
    /// El segundo assert compara sobre Sql.Replace(" ", "") (sin espacios): la aguja tampoco puede
    /// tener espacios o nunca la encuentra, pase lo que pase en el SQL real. Antes decía
    /// "is_excluded,0) = 0" (con espacios) y por eso pasaba con o sin el filtro.
    /// </summary>
    [Fact]
    public void El_sql_trae_is_excluded_pero_no_lo_filtra()
    {
        var sql = MatrizRecolector.Sql(UnaSuscripcion, seguridadGestionadaExternamente: false);
        Assert.Contains("is_excluded", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("is_excluded,0)=0", sql.Replace(" ", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void El_sql_trae_el_avance_y_la_fecha_de_deteccion()
    {
        var sql = MatrizRecolector.Sql(UnaSuscripcion, seguridadGestionadaExternamente: false);
        Assert.Contains("completion_pct", sql, StringComparison.Ordinal);
        Assert.Contains("first_seen_at", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fija el orden de las 12 columnas del SELECT contra los ordinales que MapearFila lee de
    /// SqlDataReader (0-11): los ordinales están bien hoy (contrastados uno por uno contra el
    /// esquema), pero nada impide que un reordenamiento futuro los rompa en silencio.
    /// </summary>
    [Fact]
    public void El_orden_de_columnas_del_select_coincide_con_los_ordinales_de_MapearFila()
    {
        var sql = MatrizRecolector.Sql(UnaSuscripcion, seguridadGestionadaExternamente: false);
        var columnas = new[]
        {
            "canonical_id", "matrix_code", "pillar_number", "review_scope_es", "first_seen_at",
            "impact_number", "priority_override", "projected_bit_effort", "completion_pct",
            "execution_log", "resource_count", "is_excluded",
        };
        var indices = columnas.Select(c => sql.IndexOf(c, StringComparison.Ordinal)).ToList();
        Assert.All(indices, i => Assert.True(i >= 0));
        Assert.Equal(indices, indices.OrderBy(i => i));
    }

    /// <summary>
    /// CRÍTICO de la revisión de rama: la pantalla WAF, el export a Excel y el informe mensual
    /// ocultan el pilar de Seguridad entero cuando el cliente lo gestiona externamente. Antes este
    /// SQL no llevaba el filtro ("es un parámetro de pantalla", decía el comentario); ahora replica
    /// exactamente lo que hacen esas tres salidas.
    /// </summary>
    [Fact]
    public void El_sql_excluye_el_pilar_de_seguridad_cuando_el_cliente_lo_gestiona_externamente()
    {
        var conFiltro = MatrizRecolector.Sql(UnaSuscripcion, seguridadGestionadaExternamente: true);
        var sinFiltro = MatrizRecolector.Sql(UnaSuscripcion, seguridadGestionadaExternamente: false);

        Assert.Contains("pillar_number<>3", conFiltro.Replace(" ", ""), StringComparison.Ordinal);
        Assert.DoesNotContain("pillar_number<>3", sinFiltro.Replace(" ", ""), StringComparison.Ordinal);
    }

    /// <summary>
    /// IMPORTANTE 2: sin este filtro, una recomendación con hallazgos solo en una suscripción que
    /// el cliente dejó de administrar seguía activa para siempre (SqlWafIngestionStore solo resuelve
    /// hallazgos de las suscripciones que la corrida de ingesta escaneó).
    /// </summary>
    [Fact]
    public void El_sql_filtra_por_las_suscripciones_administradas()
    {
        var sql = MatrizRecolector.Sql(["sub-a", "sub-b"], seguridadGestionadaExternamente: false);

        Assert.Contains("waf_resource_finding", sql, StringComparison.Ordinal);
        Assert.Contains("@sub0", sql, StringComparison.Ordinal);
        Assert.Contains("@sub1", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// IMPORTANTE 1 de la re-revisión: los hallazgos cargados a mano (subscription_id = 'importado',
    /// ver ClosedXmlWafImporter.CreateManualFindingsAsync) no pertenecen a ninguna suscripción real
    /// del cliente. El filtro de suscripciones administradas los tenía que dejar pasar siempre y en
    /// cambio los expulsaba: una recomendación con todos sus hallazgos en 'importado' desaparecía de
    /// la matriz del informe en cuanto el cliente tenía alguna suscripción real administrada, y en
    /// las recomendaciones mixtas (parte real, parte importada) el conteo de recursos contaba de
    /// menos por el mismo motivo.
    /// </summary>
    [Fact]
    public void El_filtro_de_administradas_no_excluye_los_hallazgos_importados()
    {
        var sql = MatrizRecolector.Sql(["sub-a", "sub-b"], seguridadGestionadaExternamente: false)
            .Replace(" ", "");

        // La condición de administradas sigue exigiéndose en el EXISTS y en el conteo (no se
        // reemplazó, se le agregó una excepción)...
        Assert.Contains("wsf.subscription_idIN(@sub0,@sub1)", sql, StringComparison.Ordinal);
        Assert.Contains("wsc.subscription_idIN(@sub0,@sub1)", sql, StringComparison.Ordinal);
        // ...pero un hallazgo 'importado' basta por sí solo, sin estar en esa lista, en los dos
        // lugares (el EXISTS que decide si la recomendación entra, y el conteo de recursos).
        Assert.Contains("ORwsf.subscription_id='importado'", sql, StringComparison.Ordinal);
        Assert.Contains("ORwsc.subscription_id='importado'", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lista vacía = nada administrado = nada que reportar, no "sin filtro" (que es lo que
    /// significa una lista vacía para WafSubscriptionFilter cuando la usa la pantalla WAF).
    /// LeerAsync corta antes de construir o correr el SQL.
    /// </summary>
    [Fact]
    public async Task LeerAsync_sin_suscripciones_administradas_devuelve_vacio_sin_conexion()
    {
        var filas = await MatrizRecolector.LeerAsync(
            conn: null!, clientId: 7, suscripcionesAdministradas: [], seguridadGestionadaExternamente: false);

        Assert.Empty(filas);
    }
}
