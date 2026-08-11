using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// No tocan la base: verifican el texto del SQL expuesto (<see cref="HallazgoResueltoRecolector.Sql"/>),
/// mismo estilo que <c>MatrizRecolectorTests</c>/<c>AdvisorRecolectorTests</c>. Tarea 3 de la entrega
/// 2d (E3): el cruce contra hallazgos resueltos de la matriz.
/// </summary>
public sealed class HallazgoResueltoRecolectorTests
{
    private static readonly string[] UnaSuscripcion = ["sub-1"];

    private static string Sql(bool seguridadGestionadaExternamente = false) =>
        HallazgoResueltoRecolector.Sql(UnaSuscripcion, seguridadGestionadaExternamente);

    [Fact]
    public void El_sql_lee_resueltos_de_waf_resource_finding_no_activos()
    {
        var sql = Sql().Replace(" ", "");
        Assert.Contains("waf_resource_finding", sql, StringComparison.Ordinal);
        Assert.Contains("f.status='resolved'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'active'", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// El join es por la terna (E3, E6): subscription_id, resource_group y resource_name viajan
    /// crudos, sin agregar ni contar — a diferencia de MatrizRecolector, que agrega a nivel
    /// recomendación.
    /// </summary>
    [Fact]
    public void El_sql_trae_la_terna_de_identidad_sin_agregar()
    {
        var sql = Sql();
        Assert.Contains("f.subscription_id", sql, StringComparison.Ordinal);
        Assert.Contains("f.resource_group", sql, StringComparison.Ordinal);
        Assert.Contains("f.resource_name", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COUNT", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void El_sql_trae_la_fecha_de_resolucion()
    {
        Assert.Contains("f.resolved_at", Sql(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Los hallazgos cargados a mano (subscription_id='importado') no pertenecen a ninguna
    /// suscripción real: nunca pueden tener una terna que cruce contra facturación, así que se
    /// excluyen por higiene (igual que AdvisorRecolector), sin la excepción que sí necesita
    /// MatrizRecolector para no subcontar resource_count.
    /// </summary>
    [Fact]
    public void El_sql_excluye_los_hallazgos_cargados_a_mano()
    {
        Assert.Contains("'importado'", Sql(), StringComparison.Ordinal);
    }

    [Fact]
    public void El_sql_excluye_el_pilar_de_seguridad_cuando_el_cliente_lo_gestiona_externamente()
    {
        var conFiltro = Sql(seguridadGestionadaExternamente: true);
        var sinFiltro = Sql(seguridadGestionadaExternamente: false);

        Assert.Contains("pillar_number<>3", conFiltro.Replace(" ", ""), StringComparison.Ordinal);
        Assert.DoesNotContain("pillar_number<>3", sinFiltro.Replace(" ", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void El_sql_filtra_por_las_suscripciones_administradas()
    {
        var sql = HallazgoResueltoRecolector.Sql(["sub-a", "sub-b"], seguridadGestionadaExternamente: false);

        Assert.Contains("f.subscription_id IN", sql, StringComparison.Ordinal);
        Assert.Contains("@sub0", sql, StringComparison.Ordinal);
        Assert.Contains("@sub1", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeerAsync_sin_suscripciones_administradas_devuelve_vacio_sin_conexion()
    {
        var filas = await HallazgoResueltoRecolector.LeerAsync(
            conn: null!, clientId: 7, suscripcionesAdministradas: [], seguridadGestionadaExternamente: false);

        Assert.Empty(filas);
    }

    /// <summary>
    /// Fija el orden de las 8 columnas del SELECT contra los ordinales que MapearFila lee de
    /// SqlDataReader (0-7), mismo estilo que el test análogo de MatrizRecolectorTests.
    /// </summary>
    [Fact]
    public void El_orden_de_columnas_del_select_coincide_con_los_ordinales_de_MapearFila()
    {
        var sql = Sql();
        var columnas = new[]
        {
            "subscription_id", "subscription_name", "resource_group", "resource_name",
            "resolved_at", "matrix_code", "review_scope_es", "pillar_number",
        };
        var indices = columnas.Select(c => sql.IndexOf(c, StringComparison.Ordinal)).ToList();
        Assert.All(indices, i => Assert.True(i >= 0));
        Assert.Equal(indices, indices.OrderBy(i => i));
    }
}
