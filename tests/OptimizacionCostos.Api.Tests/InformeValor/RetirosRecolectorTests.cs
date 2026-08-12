using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// No tocan la base: verifican el texto del SQL expuesto (RetirosRecolector.Sql) y la forma del
/// record, mismo estilo que AdvisorRecolectorTests y MatrizRecolectorTests. El comportamiento real
/// de los tres filtros/conteo (agregación de SQL Server, no simulable en texto) lo cubre
/// RetirosRecolectorDbTests, con filas mezcladas.
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
    /// Guarda de texto contra la regresión más simple: si alguien vuelve a un COUNT(*) plano,
    /// vuelve a contar filas de Service Health sin recurso (subscription_id sin
    /// azure_resource_id) como si fueran un recurso afectado. RetirosRecolectorDbTests prueba el
    /// conteo real con filas mezcladas; este test solo cubre que el texto no vuelva al patrón roto.
    /// </summary>
    [Fact]
    public void Recursos_afectados_no_es_un_count_plano()
    {
        Assert.DoesNotContain("COUNT(*)", RetirosRecolector.Sql, StringComparison.Ordinal);
        Assert.Contains("azure_resource_id IS NOT NULL", RetirosRecolector.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// BoletinAggregator separa fin de soporte (source='eol') de los retiros y lo cuenta como
    /// categoría propia (eol_products/eol_resources). Sin este filtro el bloque de retiros del
    /// informe mezclaría las dos categorías.
    /// </summary>
    [Fact]
    public void El_sql_excluye_fin_de_soporte()
    {
        Assert.Contains("source <> 'eol'", RetirosRecolector.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mismo predicado que BoletinService.ManagedSubscriptionsAsync: sin él, una fila histórica de
    /// una suscripción que el usuario dejó de administrar aparece en el informe aunque ya no
    /// aparezca en la vista del Boletín (BoletinAggregator.FilterToManaged).
    /// </summary>
    [Fact]
    public void El_sql_filtra_a_suscripciones_administradas()
    {
        Assert.Contains("client_azure_subscriptions", RetirosRecolector.Sql, StringComparison.Ordinal);
        Assert.Contains("client_azure_credentials", RetirosRecolector.Sql, StringComparison.Ordinal);
        Assert.Contains("is_managed", RetirosRecolector.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fija el orden de las 6 columnas del SELECT contra los ordinales que MapearFila lee de
    /// SqlDataReader (0-5): un reordenamiento futuro sin actualizar MapearFila lo rompe en
    /// silencio si nada más lo detecta antes.
    /// </summary>
    [Fact]
    public void El_orden_de_columnas_del_select_coincide_con_los_ordinales_de_MapearFila()
    {
        var columnas = new[]
        {
            "announcement_key", "retiring_feature", "retirement_date", "titulo",
            "accion_recomendada", "recursos_afectados",
        };
        var indices = columnas.Select(c => RetirosRecolector.Sql.IndexOf(c, StringComparison.Ordinal)).ToList();
        Assert.All(indices, i => Assert.True(i >= 0));
        Assert.Equal(indices, indices.OrderBy(i => i));
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
