using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// No tocan la base: verifican el texto del SQL expuesto (MatrizRecolector.Sql) y la forma del
/// record, mismo estilo que AdvisorRecolectorTests.
/// </summary>
public sealed class MatrizRecolectorTests
{
    [Fact]
    public void El_esfuerzo_se_devuelve_como_texto_sin_parsear()
    {
        var p = typeof(MatrizFila).GetProperty("EsfuerzoTexto");
        Assert.NotNull(p);
        Assert.Equal(typeof(string), Nullable.GetUnderlyingType(p!.PropertyType) ?? p.PropertyType);
    }

    [Fact]
    public void El_sql_trae_is_excluded_pero_no_lo_filtra()
    {
        Assert.Contains("is_excluded", MatrizRecolector.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("is_excluded,0) = 0", MatrizRecolector.Sql.Replace(" ", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void El_sql_trae_el_avance_y_la_fecha_de_deteccion()
    {
        Assert.Contains("completion_pct", MatrizRecolector.Sql, StringComparison.Ordinal);
        Assert.Contains("first_seen_at", MatrizRecolector.Sql, StringComparison.Ordinal);
    }
}
