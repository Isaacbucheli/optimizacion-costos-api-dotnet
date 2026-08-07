using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// No tocan la base: verifican el texto del SQL expuesto (RetirosRecolector.Sql) y la forma del
/// record, mismo estilo que AdvisorRecolectorTests y MatrizRecolectorTests.
/// </summary>
public sealed class RetirosRecolectorTests
{
    [Fact]
    public void El_sql_lee_de_boletin_retirement_y_solo_lo_vigente()
    {
        Assert.Contains("boletin_retirement", RetirosRecolector.Sql, StringComparison.Ordinal);
        Assert.Contains("vigente", RetirosRecolector.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// El semáforo de plazo lo calcula la calculadora con la fecha de corte del informe. Si el
    /// recolector clasificara acá, el informe cambiaría de contenido según cuándo se generó.
    /// </summary>
    [Fact]
    public void El_recolector_no_clasifica_el_plazo()
    {
        Assert.Null(typeof(RetiroFila).GetProperty("Situacion"));
        Assert.Null(typeof(RetiroFila).GetProperty("Vencido"));
    }
}
