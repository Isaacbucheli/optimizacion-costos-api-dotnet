using OptimizacionCostos.Api.Features.InformeValor.Calculo;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// D13 del plan de la entrega 2b: <c>Math.Round</c> de .NET usa redondeo bancario (el medio va al
/// vecino par), <c>Math.round</c> de JavaScript es <c>Math.floor(x+0.5)</c> (el medio siempre sube).
/// Divergen en 0.5, 2.5, -1.5 y más; coinciden en 1.5 y -2.5 porque ahí el vecino de arriba YA es
/// par. Los ocho casos de abajo cubren las dos mitades de esa simetría (donde divergen y donde no,
/// positivos y negativos) más un par de fracciones ordinarias, para no probar solo el caso feliz.
/// </summary>
public sealed class RedondeoTests
{
    [Theory]
    [InlineData(0.5, 1)]    // .NET banco -> 0 (par); JS -> 1. Divergen.
    [InlineData(1.5, 2)]    // .NET banco -> 2 (par); JS -> 2. Coinciden.
    [InlineData(2.5, 3)]    // .NET banco -> 2 (par); JS -> 3. Divergen.
    [InlineData(3.5, 4)]    // .NET banco -> 4 (par); JS -> 4. Coinciden.
    [InlineData(-0.5, 0)]   // .NET banco -> 0 (par); JS -> 0 (Math.floor(-0.5+0.5)=Math.floor(0)).
    [InlineData(-1.5, -1)]  // .NET banco -> -2 (par); JS -> -1. Divergen.
    [InlineData(-2.5, -2)]  // .NET banco -> -2 (par); JS -> -2. Coinciden.
    [InlineData(-3.5, -3)]  // .NET banco -> -4 (par); JS -> -3. Divergen.
    public void Replica_Math_round_de_JavaScript_en_los_ocho_casos_medidos(double x, double esperado)
    {
        Assert.Equal(esperado, Redondeo.ComoJs(x));
    }

    [Theory]
    [InlineData(2.4, 2)]
    [InlineData(2.6, 3)]
    [InlineData(-2.4, -2)]
    [InlineData(-2.6, -3)]
    public void Fracciones_que_no_son_un_medio_redondean_al_entero_mas_cercano(double x, double esperado)
    {
        Assert.Equal(esperado, Redondeo.ComoJs(x));
    }

    /// <summary>
    /// El patrón de la plantilla para publicar montos: <c>Math.round(x*100)/100</c>. En decimal,
    /// para no heredar el error de representación binaria de double sobre valores monetarios.
    /// 12.345 está exactamente a mitad de camino entre 12.34 y 12.35: redondeo bancario da 12.34
    /// (4 es par), JS/ComoJs da 12.35 (el medio sube).
    /// </summary>
    [Fact]
    public void Redondeo_monetario_a_dos_decimales_sube_el_medio()
    {
        Assert.Equal(12.35m, Redondeo.ComoJs(12.345m));
        Assert.Equal(12.34m, Math.Round(12.345m, 2, MidpointRounding.ToEven)); // contraste, no el sujeto bajo prueba
    }

    /// <summary>Con negativos: "sube" significa hacia +∞, o sea hacia el valor menos negativo.</summary>
    [Fact]
    public void Redondeo_monetario_negativo_tambien_sube_hacia_el_menos_negativo()
    {
        Assert.Equal(-12.34m, Redondeo.ComoJs(-12.345m));
    }

    [Fact]
    public void Sin_decimales_de_por_medio_no_cambia_un_valor_ya_exacto()
    {
        Assert.Equal(10.00m, Redondeo.ComoJs(10.00m));
    }
}
