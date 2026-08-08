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

    /// <summary>
    /// El segundo assert compara sobre Sql.Replace(" ", "") (sin espacios): la aguja tampoco puede
    /// tener espacios o nunca la encuentra, pase lo que pase en el SQL real. Antes decía
    /// "is_excluded,0) = 0" (con espacios) y por eso pasaba con o sin el filtro.
    /// </summary>
    [Fact]
    public void El_sql_trae_is_excluded_pero_no_lo_filtra()
    {
        Assert.Contains("is_excluded", MatrizRecolector.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("is_excluded,0)=0", MatrizRecolector.Sql.Replace(" ", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void El_sql_trae_el_avance_y_la_fecha_de_deteccion()
    {
        Assert.Contains("completion_pct", MatrizRecolector.Sql, StringComparison.Ordinal);
        Assert.Contains("first_seen_at", MatrizRecolector.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fija el orden de las 12 columnas del SELECT contra los ordinales que MapearFila lee de
    /// SqlDataReader (0-11): los ordinales están bien hoy (contrastados uno por uno contra el
    /// esquema), pero nada impide que un reordenamiento futuro los rompa en silencio.
    /// </summary>
    [Fact]
    public void El_orden_de_columnas_del_select_coincide_con_los_ordinales_de_MapearFila()
    {
        var columnas = new[]
        {
            "canonical_id", "matrix_code", "pillar_number", "review_scope_es", "first_seen_at",
            "impact_number", "priority_override", "projected_bit_effort", "completion_pct",
            "execution_log", "resource_count", "is_excluded",
        };
        var indices = columnas.Select(c => MatrizRecolector.Sql.IndexOf(c, StringComparison.Ordinal)).ToList();
        Assert.All(indices, i => Assert.True(i >= 0));
        Assert.Equal(indices, indices.OrderBy(i => i));
    }
}
