namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// División explícita en punto flotante, para toda regla que compare un cociente de dos enteros
/// contra una fracción (D13 del plan de la entrega 2b: tres reglas de RBAC en la plantilla lo
/// hacen, por ejemplo "más del 50% de las asignaciones sin actividad de sesión"). En C#,
/// <c>a / b</c> con <c>a</c> y <c>b</c> enteros es división ENTERA: trunca en vez de dar una
/// fracción, así que <c>sinLogin / usr</c> da casi siempre 0 y una comparación como
/// <c>0 &gt; 0.5</c> nunca dispara. La plantilla, en JavaScript, no tiene este problema porque
/// todos sus números son <c>double</c>: <c>a / b</c> ahí siempre es división real.
///
/// No hace falta memorizar dónde poner el <c>(double)</c>: <see cref="Cociente"/> nombra el
/// patrón para que un lector futuro vea la intención (comparar una proporción) en vez de una
/// división entera disfrazada de código normal.
/// </summary>
public static class Division
{
    /// <summary>Cociente en <see cref="double"/> de dos enteros. 0 si <paramref name="denominador"/> es 0
    /// (evita <see cref="DivideByZeroException"/> en una regla que no tiene por qué reventar el
    /// cálculo completo por un denominador vacío).</summary>
    public static double Cociente(int numerador, int denominador) =>
        denominador == 0 ? 0d : (double)numerador / denominador;

    /// <summary>Igual que <see cref="Cociente(int,int)"/>, pero como porcentaje (× 100). Mismo
    /// cálculo que <c>pct(a,b)</c> de la plantilla.</summary>
    public static double Porcentaje(int numerador, int denominador) => Cociente(numerador, denominador) * 100d;
}
