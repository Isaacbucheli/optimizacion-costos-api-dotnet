using OptimizacionCostos.Api.Features.InformeValor.Recolector;
using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// No tocan la base: verifican el texto del SQL expuesto (AdvisorRecolector.Sql) y las funciones
/// de mapeo puras, mismo estilo que InformeValorSchemaTests y
/// AccessReviewCompletitudTests.GetLatestFinishedRunAsync_filtra_estado_y_no_filtra_por_run_id.
/// </summary>
public sealed class AdvisorRecolectorTests
{
    [Fact]
    public void El_sql_deriva_la_categoria_de_pillar_number_y_no_del_texto()
    {
        Assert.Contains("pillar_number", AdvisorRecolector.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("advisor_category", AdvisorRecolector.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// El importador de la matriz Excel escribe subscription_id='importado' y
    /// resource_type='(importado)'. Sin este filtro el informe publica "(matriz historica)"
    /// como si fuera una suscripcion del cliente, con su porcentaje sobre el total.
    /// </summary>
    [Fact]
    public void El_sql_excluye_los_hallazgos_cargados_a_mano()
    {
        Assert.Contains("'importado'", AdvisorRecolector.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void El_sql_lee_el_ahorro_por_las_dos_rutas_priorizando_la_del_sync()
    {
        var i = AdvisorRecolector.Sql.IndexOf("annualSavingsAmount", StringComparison.Ordinal);
        var j = AdvisorRecolector.Sql.IndexOf("Potential Annual Cost Savings", StringComparison.Ordinal);
        Assert.True(i > 0 && j > 0, "faltan las dos rutas del ahorro");
        Assert.True(i < j, "la ruta del sync tiene que ir primero en el COALESCE");
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
