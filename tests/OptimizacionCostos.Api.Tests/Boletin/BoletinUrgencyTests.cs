using OptimizacionCostos.Api.Features.Boletin;

namespace OptimizacionCostos.Api.Tests.Boletin;

public class BoletinUrgencyTests
{
    private static readonly DateOnly Hoy = new(2026, 7, 31);

    [Fact]
    public void SinFechaCuandoNoHayFecha() =>
        Assert.Equal(BoletinUrgency.SinFecha, BoletinUrgency.Classify(null, Hoy));

    [Fact]
    public void RetiradoCuandoLaFechaYaPaso() =>
        Assert.Equal(BoletinUrgency.Retirado, BoletinUrgency.Classify(new DateOnly(2026, 7, 30), Hoy));

    [Fact]
    public void HoyMismoCuentaComoProximo() =>
        Assert.Equal(BoletinUrgency.Proximo, BoletinUrgency.Classify(Hoy, Hoy));

    [Fact]
    public void Dia182EsProximoYDia183EsProgramado()
    {
        Assert.Equal(BoletinUrgency.Proximo, BoletinUrgency.Classify(Hoy.AddDays(182), Hoy));
        Assert.Equal(BoletinUrgency.Programado, BoletinUrgency.Classify(Hoy.AddDays(183), Hoy));
    }
}
