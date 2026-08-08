using OptimizacionCostos.Api.Features.InformeValor.Calculo;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>D13 del plan de la entrega 2b: fechas de origen en formato de Estados Unidos (mes
/// primero) y resolución de zona horaria en America/Guayaquil, una sola vez.</summary>
public sealed class FechasTests
{
    [Theory]
    [InlineData("3/5/2026", 2026, 3, 5)]    // sin ceros a la izquierda: 5 de marzo, NO 3 de mayo
    [InlineData("03/05/2026", 2026, 3, 5)]  // con ceros a la izquierda
    [InlineData("12/31/2026", 2026, 12, 31)]
    [InlineData("1/1/2026", 2026, 1, 1)]
    public void Interpreta_mes_primero_nunca_dia_primero(string raw, int anio, int mes, int dia)
    {
        Assert.True(Fechas.TryParseFormatoEeuu(raw, out var fecha));
        Assert.Equal(new DateOnly(anio, mes, dia), fecha);
    }

    /// <summary>
    /// El defecto que se corrige: la plantilla interpreta día/mes siempre (un ternario que no
    /// hace nada), así que "13/5/2026" —trece de mayo, imposible como día en formato mes/día
    /// solo si el 13 fuera el mes— desborda al año siguiente en JavaScript (Date.UTC no valida el
    /// mes). Acá, con el mes fijo explícitamente en la primera posición, un mes fuera de 1-12 no
    /// convierte: es un aviso para quien llama (false), nunca un desborde silencioso.
    /// </summary>
    [Theory]
    [InlineData("13/5/2026")]   // "mes" 13 fuera de rango
    [InlineData("00/5/2026")]   // "mes" 0 fuera de rango
    [InlineData("31/12/2026")]  // sería válido como día/mes; como mes/día, "31" no es un mes
    [InlineData("no es una fecha")]
    [InlineData("")]
    [InlineData("   ")]
    public void Un_mes_fuera_de_rango_o_texto_ilegible_no_convierte(string raw)
    {
        Assert.False(Fechas.TryParseFormatoEeuu(raw, out _));
    }

    [Fact]
    public void Null_no_convierte()
    {
        Assert.False(Fechas.TryParseFormatoEeuu(null, out _));
    }

    /// <summary>
    /// Guayaquil es UTC-5 fijo: un instante de madrugada en UTC cae en el día CALENDARIO anterior
    /// en Guayaquil. Si la resolución de zona horaria no se aplicara (o se aplicara mal), este
    /// caso daría el mismo día en las dos zonas y el test no distinguiría nada.
    /// </summary>
    [Fact]
    public void Resuelve_la_fecha_calendario_de_guayaquil_no_la_de_utc()
    {
        var madrugadaUtc = new DateTimeOffset(2026, 1, 15, 2, 0, 0, TimeSpan.Zero); // 02:00 UTC
        Assert.Equal(new DateOnly(2026, 1, 14), Fechas.ResolverFechaEnGuayaquil(madrugadaUtc));

        var medioDiaUtc = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero); // 07:00 Guayaquil
        Assert.Equal(new DateOnly(2026, 1, 15), Fechas.ResolverFechaEnGuayaquil(medioDiaUtc));
    }

    /// <summary>Guayaquil no observa horario de verano: el desplazamiento es -5 todo el año, sin
    /// excepción de temporada (a diferencia de zonas con DST, donde este test fallaría en algún
    /// mes si la implementación asumiera un offset fijo incorrecto).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    public void El_desplazamiento_es_siempre_menos_cinco_horas_sin_horario_de_verano(int mes)
    {
        var instanteUtc = new DateTimeOffset(2026, mes, 15, 0, 0, 0, TimeSpan.Zero);
        var offset = Fechas.ZonaGuayaquil.GetUtcOffset(instanteUtc);
        Assert.Equal(TimeSpan.FromHours(-5), offset);
    }
}
