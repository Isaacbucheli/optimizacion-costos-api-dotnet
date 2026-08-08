using OptimizacionCostos.Api.Features.InformeValor.Calculo;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// D13 del plan de la entrega 2b: tres reglas de RBAC en la plantilla comparan un cociente de dos
/// enteros contra una fracción (p. ej. "más del 50% de las asignaciones sin actividad de
/// sesión"). En C#, <c>a / b</c> con los dos operandos <see langword="int"/> es división ENTERA:
/// trunca, así que la regla no dispara casi nunca. La plantilla, en JavaScript, no tiene este
/// problema porque ahí todo número es <c>double</c>.
/// </summary>
public sealed class DivisionTests
{
    /// <summary>La trampa, demostrada con el mismo caso que la motiva: 6 de 10 asignaciones sin
    /// actividad de sesión (60%) tendría que disparar una regla con umbral 50%.</summary>
    [Fact]
    public void Cociente_dispara_una_regla_donde_la_division_entera_de_C_no()
    {
        int sinActividad = 6, usuarios = 10;

        Assert.Equal(0, sinActividad / usuarios); // la trampa: division entera trunca a 0
        Assert.True(Division.Cociente(sinActividad, usuarios) > 0.5); // el cociente real si dispara
    }

    [Theory]
    [InlineData(1, 4, 0.25)]
    [InlineData(3, 4, 0.75)]
    [InlineData(0, 4, 0.0)]
    [InlineData(5, 5, 1.0)]
    public void Cociente_da_la_fraccion_real(int numerador, int denominador, double esperado)
    {
        Assert.Equal(esperado, Division.Cociente(numerador, denominador), 10);
    }

    [Fact]
    public void Cociente_con_denominador_cero_no_revienta_la_regla_completa()
    {
        Assert.Equal(0d, Division.Cociente(5, 0));
    }

    [Fact]
    public void Porcentaje_es_el_cociente_por_cien()
    {
        Assert.Equal(60.0, Division.Porcentaje(6, 10), 10);
    }
}
