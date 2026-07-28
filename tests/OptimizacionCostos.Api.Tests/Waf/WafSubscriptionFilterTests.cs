using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Filtro por suscripción: parser, fragmentos SQL y reponderación del Advisor Score. Sin BD.
/// </summary>
public class WafSubscriptionFilterTests
{
    // ---------- Parser ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    public void Parse_vacio_no_activa_el_filtro(string? raw)
    {
        var subs = WafSubscriptionFilter.Parse(raw);
        Assert.Empty(subs);
        Assert.False(WafSubscriptionFilter.IsActive(subs));
    }

    [Fact]
    public void Parse_recorta_espacios_y_deduplica_sin_distinguir_mayusculas()
    {
        var subs = WafSubscriptionFilter.Parse(" AAA , bbb,aaa ,,BBB ");
        Assert.Equal(["AAA", "bbb"], subs); // conserva el orden y la primera grafía
    }

    // ---------- Fragmentos SQL ----------

    [Fact]
    public void Sin_filtro_los_fragmentos_no_alteran_la_consulta()
    {
        var none = WafSubscriptionFilter.Parse(null);
        Assert.Equal("", WafSubscriptionFilter.ExistsPredicate("r", none));
        Assert.Equal("", WafSubscriptionFilter.FindingPredicate("f", none));
        // Ruta rápida: se sigue leyendo la columna denormalizada.
        Assert.Equal("r.resource_count", WafSubscriptionFilter.ResourceCountExpr("r", none));
    }

    [Fact]
    public void Con_filtro_el_conteo_deja_de_leer_la_columna_denormalizada()
    {
        var subs = WafSubscriptionFilter.Parse("a,b");
        var expr = WafSubscriptionFilter.ResourceCountExpr("r", subs);

        Assert.DoesNotContain("r.resource_count", expr);
        Assert.Contains("waf_resource_finding", expr);
        Assert.Contains("status = 'active'", expr);
        Assert.Contains("@sub0,@sub1", expr);
    }

    [Fact]
    public void El_predicado_exige_hallazgo_activo_en_la_seleccion()
    {
        var predicate = WafSubscriptionFilter.ExistsPredicate("r", WafSubscriptionFilter.Parse("a"));
        Assert.Contains("EXISTS", predicate);
        Assert.Contains("wsf.recommendation_id = r.recommendation_id", predicate);
        Assert.Contains("wsf.status = 'active'", predicate);
    }

    // ---------- Advisor Score ponderado ----------

    private const string Breakdown = """
        [
          {"subscription_id":"sub-a","subscription_name":"prod","scores":{"3":{"score":50,"weight":100},"4":{"score":80,"weight":10}}},
          {"subscription_id":"sub-b","subscription_name":"dev","scores":{"3":{"score":100,"weight":100}}}
        ]
        """;

    [Fact]
    public void Score_de_una_sola_suscripcion_es_el_suyo()
    {
        var (pillars, applied) = WafSubscriptionFilter.FilterScores(Breakdown, ["sub-a"]);
        Assert.True(applied);
        Assert.Equal(50m, pillars[3]);
        Assert.Equal(80m, pillars[4]);
    }

    [Fact]
    public void Score_de_varias_es_el_promedio_ponderado_por_peso()
    {
        var (pillars, applied) = WafSubscriptionFilter.FilterScores(Breakdown, ["sub-a", "sub-b"]);
        Assert.True(applied);
        Assert.Equal(75m, pillars[3]); // (50*100 + 100*100) / 200
        Assert.Equal(80m, pillars[4]); // solo sub-a aporta al pilar 4
    }

    [Fact]
    public void Suscripcion_inexistente_no_deja_pilares()
    {
        var (pillars, applied) = WafSubscriptionFilter.FilterScores(Breakdown, ["sub-zzz"]);
        Assert.True(applied); // el breakdown existe: el filtro sí se pudo aplicar
        Assert.Empty(pillars);
    }

    [Fact]
    public void Sin_breakdown_el_filtro_no_se_puede_aplicar()
    {
        Assert.False(WafSubscriptionFilter.FilterScores(null, ["sub-a"]).Applied);
        Assert.False(WafSubscriptionFilter.FilterScores("", ["sub-a"]).Applied);
        Assert.False(WafSubscriptionFilter.FilterScores("[]", ["sub-a"]).Applied);
    }

    [Fact]
    public void Breakdown_corrupto_avisa_en_vez_de_inventar()
    {
        Assert.False(WafSubscriptionFilter.FilterScores("{no es json", ["sub-a"]).Applied);
    }

    [Fact]
    public void Peso_cero_deja_el_pilar_sin_dato()
    {
        const string zeroWeight = """[{"subscription_id":"s","scores":{"3":{"score":40,"weight":0}}}]""";
        var (pillars, applied) = WafSubscriptionFilter.FilterScores(zeroWeight, ["s"]);
        Assert.False(applied); // ninguna entrada utilizable => no hay reponderación posible
        Assert.Empty(pillars);
    }

    [Fact]
    public void Sin_seleccion_no_se_recalcula_nada()
    {
        var (pillars, applied) = WafSubscriptionFilter.FilterScores(Breakdown, []);
        Assert.False(applied);
        Assert.Empty(pillars);
    }
}
