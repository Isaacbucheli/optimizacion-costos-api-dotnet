namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Redondeo compatible con <c>Math.round</c> de JavaScript, que es lo que usa la plantilla en
/// cada cifra que se muestra (D13 del plan de la entrega 2b). <c>Math.Round</c> de .NET usa
/// redondeo bancario (<see cref="MidpointRounding.ToEven"/> por defecto): el medio va al vecino
/// PAR, no siempre hacia arriba. <c>Math.round</c> de JavaScript, en cambio, es
/// <c>Math.floor(x + 0.5)</c>: el medio siempre redondea hacia arriba (hacia +∞), incluso para
/// negativos.
///
/// Difieren cada vez que el vecino "hacia arriba" es impar: 0.5 (.NET banco → 0; JS → 1), 2.5
/// (.NET banco → 2; JS → 3), 4.5 (.NET banco → 4; JS → 5) y así. Con negativos la intuición falla
/// más todavía: -1.5 en JS da -1 (redondea hacia +∞, o sea "hacia arriba" en la recta numérica,
/// que para un negativo es hacia cero), no -2. Usar <c>Math.Round</c> de .NET en cualquier cifra
/// que la plantilla también redondea produce una diferencia silenciosa de paridad que no es un
/// error de cálculo, sino de qué función de redondeo se usó.
/// </summary>
public static class Redondeo
{
    /// <summary>Replica <c>Math.round(x)</c> de JavaScript. Sin decimales.</summary>
    public static double ComoJs(double x) => Math.Floor(x + 0.5);

    /// <summary>
    /// Replica el patrón <c>Math.round(x * 100) / 100</c> que usa la plantilla para publicar
    /// montos con 2 decimales (y variantes con otras potencias de 10 vía <paramref name="decimales"/>).
    /// Escala, redondea como JS y vuelve a escalar: hacerlo en <see cref="decimal"/> evita el
    /// error de representación binaria de <see cref="double"/> para valores monetarios.
    /// </summary>
    public static decimal ComoJs(decimal x, int decimales = 2)
    {
        var factor = Pow10(decimales);
        return Math.Floor(x * factor + 0.5m) / factor;
    }

    private static decimal Pow10(int decimales)
    {
        decimal factor = 1m;
        for (var i = 0; i < decimales; i++) factor *= 10m;
        return factor;
    }
}
