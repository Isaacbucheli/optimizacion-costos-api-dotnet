namespace OptimizacionCostos.Api.Tests.InformeValor;

using OptimizacionCostos.Api.Features.InformeValor.Recolector;

public class BarridoResueltoRecolectorTests
{
    /// <summary>Solo el estado 'resuelto', y el hallazgo tal como se vio la última vez.</summary>
    [Fact]
    public void El_sql_junta_el_estado_con_el_hallazgo_de_su_ultimo_scan()
    {
        var sql = BarridoResueltoRecolector.Sql;
        Assert.Contains("dbo.optimization_finding_state s", sql);
        Assert.Contains("f.scan_id = s.last_seen_scan_id", sql);
        Assert.Contains("s.state = 'resuelto'", sql);
        Assert.Contains("s.resolved_by_kind", sql);
        Assert.Contains("f.estimated_monthly_savings", sql);
    }

    /// <summary>Sin barrido corrido NO es "cero hallazgos resueltos": es eje no medido (D9).</summary>
    [Fact]
    public void Sin_barrido_el_registro_no_esta_medido()
    {
        var r = RegistroBarrido.SinBarrido();
        Assert.False(r.Medido);
        Assert.Empty(r.Filas);
        Assert.NotEmpty(r.Motivo!);
    }

    /// <summary>La doble puerta del spec: sin permiso, la sección declara y no inventa.</summary>
    [Fact]
    public void No_autorizado_declara_el_motivo()
    {
        var r = RegistroBarrido.NoAutorizado("El barrido de optimización requiere permisos que este usuario no tiene.");
        Assert.False(r.Medido);
        Assert.Contains("permisos", r.Motivo!);
    }

    /// <summary>
    /// Fija el orden de las 11 columnas del SELECT contra los ordinales que MapearFila lee de
    /// SqlDataReader (0-10), mismo estilo que el test análogo de CronologiaRecolectorTests /
    /// MatrizRecolectorTests: MapearFila lee por posición, no por nombre, así que el orden del
    /// SELECT debe coincidir exactamente.
    /// </summary>
    [Fact]
    public void El_orden_de_columnas_del_select_coincide_con_los_ordinales_de_MapearFila()
    {
        var sql = BarridoResueltoRecolector.Sql;
        var columnas = new[]
        {
            "check_id", "subscription_id", "azure_resource_id", "resource_name", "resource_type",
            "estimated_monthly_savings", "currency", "updated_at", "updated_by",
            "resolved_by_kind", "notes",
        };
        var indices = columnas.Select(c => sql.IndexOf(c, StringComparison.Ordinal)).ToList();
        Assert.All(indices, i => Assert.True(i >= 0));
        Assert.Equal(indices, indices.OrderBy(i => i));
    }
}
