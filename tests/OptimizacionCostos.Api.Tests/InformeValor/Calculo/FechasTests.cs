using OptimizacionCostos.Api.Features.InformeValor.Calculo;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>D13 del plan de la entrega 2b: resolución de zona horaria en America/Guayaquil, una
/// sola vez. (El otro problema de fecha de D13 —fechas de origen en formato de Estados Unidos—
/// tenía su propio helper y sus propios tests acá; se borraron los dos en la Tarea 8 al confirmar
/// que <c>Fechas.TryParseFormatoEeuu</c> no tenía ningún llamador de producción: ver el comentario
/// de clase de <see cref="Fechas"/>.)</summary>
public sealed class FechasTests
{
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
