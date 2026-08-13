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
}
