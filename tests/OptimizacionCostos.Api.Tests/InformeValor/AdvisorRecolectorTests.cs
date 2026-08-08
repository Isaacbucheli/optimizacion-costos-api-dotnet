using OptimizacionCostos.Api.Features.InformeValor.Recolector;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// No tocan la base: verifican el texto del SQL expuesto (AdvisorRecolector.Sql) y las funciones
/// de mapeo puras, mismo estilo que InformeValorSchemaTests y
/// AccessReviewCompletitudTests.GetLatestFinishedRunAsync_filtra_estado_y_no_filtra_por_run_id.
/// Desde la revisión de rama de la Entrega 2a, Sql es una función de la lista de suscripciones
/// administradas y de la bandera de seguridad gestionada externamente (antes una constante fija).
/// </summary>
public sealed class AdvisorRecolectorTests
{
    private static readonly string[] UnaSuscripcion = ["sub-1"];

    private static string Sql(bool seguridadGestionadaExternamente = false) =>
        AdvisorRecolector.Sql(UnaSuscripcion, seguridadGestionadaExternamente);

    [Fact]
    public void El_sql_deriva_la_categoria_de_pillar_number_y_no_del_texto()
    {
        Assert.Contains("pillar_number", Sql(), StringComparison.Ordinal);
        Assert.DoesNotContain("advisor_category", Sql(), StringComparison.Ordinal);
    }

    /// <summary>
    /// El importador de la matriz Excel escribe subscription_id='importado' y
    /// resource_type='(importado)'. Sin este filtro el informe publica "(matriz historica)"
    /// como si fuera una suscripcion del cliente, con su porcentaje sobre el total.
    /// </summary>
    [Fact]
    public void El_sql_excluye_los_hallazgos_cargados_a_mano()
    {
        Assert.Contains("'importado'", Sql(), StringComparison.Ordinal);
    }

    [Fact]
    public void El_sql_lee_el_ahorro_por_las_dos_rutas_priorizando_la_del_sync()
    {
        var sql = Sql();
        var i = sql.IndexOf("annualSavingsAmount", StringComparison.Ordinal);
        var j = sql.IndexOf("Potential Annual Cost Savings", StringComparison.Ordinal);
        Assert.True(i > 0 && j > 0, "faltan las dos rutas del ahorro");
        Assert.True(i < j, "la ruta del sync tiene que ir primero en el COALESCE");
    }

    /// <summary>
    /// IMPORTANTE 6: la clave de moneda de la ruta del CSV ("Potential Annual Cost Savings") es
    /// "Potential Cost Savings Currency" (confirmada contra la base real: aparece en las mismas
    /// filas que traen el monto por esa ruta). Antes moneda_ahorro solo leía la clave de la ruta
    /// del sync, así que esas filas salían con el monto y sin moneda. La clave de la ruta del sync
    /// (extendedProperties.savingsCurrency) queda igual: sigue sin verificar, no se tocó.
    /// </summary>
    [Fact]
    public void El_sql_lee_la_moneda_del_ahorro_por_las_dos_rutas()
    {
        var sql = Sql();
        Assert.Contains("extendedProperties.savingsCurrency", sql, StringComparison.Ordinal);
        Assert.Contains("Potential Cost Savings Currency", sql, StringComparison.Ordinal);

        // moneda_ahorro tiene que ser un COALESCE de las dos, no solo la primera con la segunda
        // suelta en otro lado del SELECT.
        var inicioMoneda = sql.IndexOf("AS moneda_ahorro", StringComparison.Ordinal);
        Assert.True(inicioMoneda > 0);
        var bloqueMoneda = sql[..inicioMoneda];
        var monedaSync = bloqueMoneda.LastIndexOf("extendedProperties.savingsCurrency", StringComparison.Ordinal);
        var monedaCsv = bloqueMoneda.LastIndexOf("Potential Cost Savings Currency", StringComparison.Ordinal);
        Assert.True(monedaSync > 0 && monedaCsv > monedaSync,
            "moneda_ahorro debe leer primero la ruta del sync y despues la del CSV, igual que ahorro_anual");
    }

    /// <summary>
    /// CRÍTICO de la revisión de rama: mismo filtro que MatrizRecolector (ver su test análogo) y
    /// por la misma razón — antes este recolector no traía la bandera, así que el lado de Advisor
    /// del informe no podía replicar lo que ya hacen la pantalla WAF, el export a Excel y el
    /// informe mensual del lado de la matriz.
    /// </summary>
    [Fact]
    public void El_sql_excluye_el_pilar_de_seguridad_cuando_el_cliente_lo_gestiona_externamente()
    {
        var conFiltro = Sql(seguridadGestionadaExternamente: true);
        var sinFiltro = Sql(seguridadGestionadaExternamente: false);

        Assert.Contains("pillar_number<>3", conFiltro.Replace(" ", ""), StringComparison.Ordinal);
        Assert.DoesNotContain("pillar_number<>3", sinFiltro.Replace(" ", ""), StringComparison.Ordinal);
    }

    /// <summary>IMPORTANTE 2: ver el test análogo de MatrizRecolectorTests.</summary>
    [Fact]
    public void El_sql_filtra_por_las_suscripciones_administradas()
    {
        var sql = AdvisorRecolector.Sql(["sub-a", "sub-b"], seguridadGestionadaExternamente: false);

        Assert.Contains("f.subscription_id IN", sql, StringComparison.Ordinal);
        Assert.Contains("@sub0", sql, StringComparison.Ordinal);
        Assert.Contains("@sub1", sql, StringComparison.Ordinal);
    }

    /// <summary>Ver el test análogo de MatrizRecolectorTests: misma semántica, mismo motivo.</summary>
    [Fact]
    public async Task LeerAsync_sin_suscripciones_administradas_devuelve_vacio_sin_conexion()
    {
        var filas = await AdvisorRecolector.LeerAsync(
            conn: null!, clientId: 7, suscripcionesAdministradas: [], seguridadGestionadaExternamente: false);

        Assert.Empty(filas);
    }

    /// <summary>
    /// D11 de la entrega 2b: la identidad de un recurso es suscripción + grupo + nombre, igual que
    /// en facturación. Antes este SQL no traía resource_group (la Tarea 2 lo dejó documentado como
    /// pendiente en PosturaModelo): sin él, NumRecursos solo podía deduplicar por nombre, y dos
    /// recursos homónimos en grupos distintos de la misma suscripción contaban como uno.
    /// </summary>
    [Fact]
    public void El_sql_trae_el_grupo_de_recursos()
    {
        Assert.Contains("f.resource_group", Sql(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Fija el orden de las 14 columnas del SELECT contra los ordinales que MapearFila lee de
    /// SqlDataReader (0-13), mismo estilo que el test análogo de MatrizRecolectorTests: un
    /// reordenamiento futuro (por ejemplo, al agregar otra columna) rompe esto en silencio si no se
    /// actualiza MapearFila a la vez.
    /// </summary>
    [Fact]
    public void El_orden_de_columnas_del_select_coincide_con_los_ordinales_de_MapearFila()
    {
        var sql = Sql();
        var columnas = new[]
        {
            "pillar_number", "impact_number", "advisor_name", "advisor_name_en", "canonical_id",
            "matrix_code", "source", "subscription_id", "subscription_name", "resource_group",
            "resource_name", "resource_type", "ahorro_anual", "moneda_ahorro",
        };
        var indices = columnas.Select(c => sql.IndexOf(c, StringComparison.Ordinal)).ToList();
        Assert.All(indices, i => Assert.True(i >= 0));
        Assert.Equal(indices, indices.OrderBy(i => i));
    }

    [Theory]
    [InlineData(1, "Alto")]
    [InlineData(2, "Medio")]
    [InlineData(3, "Bajo")]
    [InlineData(null, "")]
    public void El_impacto_se_mapea_desde_el_numero(int? numero, string esperado)
        => Assert.Equal(esperado, AdvisorRecolector.EtiquetaImpacto(numero));

    [Fact]
    public void Las_etiquetas_de_pilar_son_las_de_la_pantalla_de_la_matriz()
    {
        // Hay tres juegos de etiquetas compitiendo en el repo. El informe usa el mismo que
        // el consultor ve en la matriz, o los dos bloques de la misma pagina se contradicen.
        for (var p = 1; p <= 5; p++)
            Assert.Equal(SqlWafRecommendationStore.PillarSectionNames[p], AdvisorRecolector.EtiquetaPilar(p));
    }
}
