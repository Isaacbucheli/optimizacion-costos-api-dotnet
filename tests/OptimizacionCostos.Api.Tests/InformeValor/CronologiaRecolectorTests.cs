namespace OptimizacionCostos.Api.Tests.InformeValor;

using OptimizacionCostos.Api.Features.InformeValor.Recolector;

public class CronologiaRecolectorTests
{
    /// <summary>La cronología sale de la bitácora del tracking, con nombre de recomendación.</summary>
    [Fact]
    public void El_sql_junta_bitacora_recomendacion_y_canonica()
    {
        var sql = CronologiaRecolector.Sql(seguridadGestionadaExternamente: false);
        Assert.Contains("dbo.waf_tracking_history", sql);
        Assert.Contains("dbo.waf_recommendation r", sql);
        Assert.Contains("dbo.waf_recommendation_canonical c", sql);
        Assert.Contains("ORDER BY h.changed_at", sql);
        Assert.DoesNotContain("pillar_number <>", sql);
    }

    /// <summary>Seguridad gestionada externamente oculta el pilar 3 en TODO el informe,
    /// la cronología incluida: un hito de una recomendación oculta delataría el hallazgo.</summary>
    [Fact]
    public void Con_seguridad_externa_el_pilar_3_queda_fuera()
    {
        var sql = CronologiaRecolector.Sql(seguridadGestionadaExternamente: true);
        Assert.Contains("pillar_number <>", sql);
    }

    /// <summary>
    /// Fija el orden de las 8 columnas del SELECT contra los ordinales que MapearFila lee de
    /// SqlDataReader (0-7). MapearFila lee por posición, no por nombre: el orden del SELECT
    /// debe coincidir exactamente.
    /// </summary>
    [Fact]
    public void El_orden_de_columnas_del_select_coincide_con_los_ordinales_de_MapearFila()
    {
        var sql = CronologiaRecolector.Sql(seguridadGestionadaExternamente: false);
        var columnas = new[]
        {
            "changed_at", "field_changed", "old_value", "new_value",
            "changed_by", "matrix_code", "review_scope_es", "pillar_number",
        };
        var indices = columnas.Select(c => sql.IndexOf(c, StringComparison.Ordinal)).ToList();
        Assert.All(indices, i => Assert.True(i >= 0));
        Assert.Equal(indices, indices.OrderBy(i => i));
    }
}
