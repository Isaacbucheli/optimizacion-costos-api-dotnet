using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

/// <summary>
/// El ambiente se infiere del nombre de la suscripción, y de esa inferencia depende el hallazgo de
/// segregación. Un error acá le diría al cliente que no separa ambientes cuando sí lo hace, así que
/// los casos borde (PRE-PROD, PRODUCTOS) son lo importante de estos tests.
/// </summary>
public class AccessReviewEnvironmentTests
{
    [Theory]
    [InlineData("SAPPRD")]
    [InlineData("Analítica Avanzada - PRD")]
    [InlineData("Aplicaciones - PRD")]
    [InlineData("CorporativoPRD")]
    [InlineData("sub-produccion")]
    [InlineData("Contoso PROD")]
    public void Reconoce_produccion(string name) =>
        Assert.Equal(AccessReviewEnvironment.Produccion, AccessReviewEnvironment.Classify(name));

    [Theory]
    [InlineData("SAPQAS")]
    [InlineData("AnaliticaQAS")]
    [InlineData("Contoso QA")]
    [InlineData("sub-staging")]
    [InlineData("Ambiente de pruebas")]
    [InlineData("UAT Aplicaciones")]
    public void Reconoce_preproduccion(string name) =>
        Assert.Equal(AccessReviewEnvironment.Preproduccion, AccessReviewEnvironment.Classify(name));

    [Theory]
    [InlineData("AnaliticaDEV")]
    [InlineData("SAPSBX")]
    [InlineData("BDelta-Laboratorio")]
    [InlineData("Experimentacion")]
    [InlineData("sub-sandbox")]
    public void Reconoce_desarrollo(string name) =>
        Assert.Equal(AccessReviewEnvironment.Desarrollo, AccessReviewEnvironment.Classify(name));

    [Fact]
    public void Preprod_no_se_clasifica_como_produccion()
    {
        // "PRE-PROD" contiene "prod": sin la precedencia de preproduccion quedaria mal clasificado,
        // y el hallazgo de segregacion compararia produccion contra produccion.
        Assert.Equal(AccessReviewEnvironment.Preproduccion, AccessReviewEnvironment.Classify("PRE-PROD"));
        Assert.Equal(AccessReviewEnvironment.Preproduccion, AccessReviewEnvironment.Classify("Contoso PreProd"));
    }

    [Theory]
    // Casos reales de clientes que NO son ambientes y que un sufijo suelto clasificaría mal.
    [InlineData("Ambiente de Redes")]        // termina en "des"
    [InlineData("PRODUCTOS")]                // contiene "prod"
    [InlineData("Suscripcion Corporativa")]
    [InlineData("Sentinel")]
    [InlineData("SHMS Intelltech")]
    [InlineData("Proyectos Especiales")]     // empieza con "pro"
    public void No_clasifica_por_substring_suelto(string name) =>
        Assert.Equal(AccessReviewEnvironment.Desconocido, AccessReviewEnvironment.Classify(name));

    [Theory]
    // Nombres reales con el ambiente pegado al final: acá el sufijo SÍ debe contar.
    [InlineData("SAPPRD", AccessReviewEnvironment.Produccion)]
    [InlineData("CorporativoPRD", AccessReviewEnvironment.Produccion)]
    [InlineData("AnaliticaDEV", AccessReviewEnvironment.Desarrollo)]
    [InlineData("SAPSBX", AccessReviewEnvironment.Desarrollo)]
    [InlineData("AnaliticaQAS", AccessReviewEnvironment.Preproduccion)]
    [InlineData("UtilidadesQAS", AccessReviewEnvironment.Preproduccion)]
    public void Reconoce_el_ambiente_pegado_al_final(string name, string expected) =>
        Assert.Equal(expected, AccessReviewEnvironment.Classify(name));

    [Fact]
    public void Sin_nombre_es_desconocido()
    {
        Assert.Equal(AccessReviewEnvironment.Desconocido, AccessReviewEnvironment.Classify(null));
        Assert.Equal(AccessReviewEnvironment.Desconocido, AccessReviewEnvironment.Classify("   "));
    }

    [Fact]
    public void Es_insensible_al_casing_y_a_los_separadores()
    {
        foreach (var n in new[] { "sapprd", "SAP_PRD", "SAP-PRD", "SAP.PRD", "SAP PRD" })
            Assert.Equal(AccessReviewEnvironment.Produccion, AccessReviewEnvironment.Classify(n));
    }

    [Fact]
    public void Nombre_mixto_toma_el_ambiente_menos_productivo()
    {
        // "Aplicaciones - DEV/QAS" no es produccion, y entre dev y qas da igual cual
        // gane siempre que NO sea produccion (es lo que decide el hallazgo de segregacion).
        var r = AccessReviewEnvironment.Classify("Aplicaciones - DEV/QAS");

        Assert.NotEqual(AccessReviewEnvironment.Produccion, r);
        Assert.NotEqual(AccessReviewEnvironment.Desconocido, r);
    }

    [Fact]
    public void IsProduccion_solo_para_produccion()
    {
        Assert.True(AccessReviewEnvironment.IsProduccion(AccessReviewEnvironment.Produccion));
        Assert.False(AccessReviewEnvironment.IsProduccion(AccessReviewEnvironment.Preproduccion));
        Assert.False(AccessReviewEnvironment.IsProduccion(AccessReviewEnvironment.Desarrollo));
        Assert.False(AccessReviewEnvironment.IsProduccion(AccessReviewEnvironment.Desconocido));
    }
}
