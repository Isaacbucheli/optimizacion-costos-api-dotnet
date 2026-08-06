using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Tests.Data;

/// <summary>
/// Enfriamiento del refresco de Advisor. La guarda que ya existía ("no crear una corrida si hay otra
/// activa") no alcanzaba: una corrida dura entre 10 y 30 segundos, así que peticiones espaciadas la
/// esquivan siempre. Es lo que pasó el 2026-08-03, cuando el escaneo ejecutó seis consultas reales a
/// Advisor en cuarenta minutos contra el tenant de un cliente, que es la única operación de la API que
/// **escribe** en ARM de la suscripción ajena.
///
/// El límite de tasa global es la capa ancha; esto es la regla de negocio.
/// </summary>
public sealed class CooldownWindowTests
{
    private static readonly TimeSpan Quince = TimeSpan.FromMinutes(15);

    [Fact]
    public void Sin_corrida_previa_se_puede_consultar()
    {
        Assert.Null(CooldownWindow.Remaining(null, Quince));
    }

    [Theory]
    [InlineData(0)]    // recién terminó
    [InlineData(1)]
    [InlineData(300)]  // 5 minutos
    [InlineData(899)]  // un segundo antes de cumplir los 15
    public void Antes_del_enfriamiento_devuelve_lo_que_falta(int transcurrido)
    {
        var falta = CooldownWindow.Remaining(transcurrido, Quince);

        Assert.NotNull(falta);
        Assert.Equal(Quince - TimeSpan.FromSeconds(transcurrido), falta);
    }

    [Theory]
    [InlineData(900)]   // el borde exacto: 15 minutos ya cumplidos
    [InlineData(901)]
    [InlineData(86400)] // un día después
    public void Cumplido_el_enfriamiento_se_puede_consultar(int transcurrido)
    {
        Assert.Null(CooldownWindow.Remaining(transcurrido, Quince));
    }

    [Fact]
    public void Con_enfriamiento_en_cero_nunca_bloquea()
    {
        // ADVISOR_SYNC_COOLDOWN_MINUTES=0 es la válvula de escape para desactivarlo en producción.
        Assert.Null(CooldownWindow.Remaining(0, TimeSpan.Zero));
        Assert.Null(CooldownWindow.Remaining(null, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100000)]
    public void Un_transcurrido_negativo_no_alarga_el_enfriamiento(int transcurrido)
    {
        // DATEDIFF puede dar negativo si el reloj salta hacia atrás. Sin acotar a 0, el tiempo
        // restante saldría mayor que el enfriamiento configurado y en el caso extremo no vencería.
        var falta = CooldownWindow.Remaining(transcurrido, Quince);

        Assert.Equal(Quince, falta);
        Assert.True(falta <= Quince, "el restante nunca puede superar el enfriamiento configurado");
    }

    [Fact]
    public void El_restante_siempre_cabe_entre_cero_y_el_enfriamiento()
    {
        // Barrido del rango completo: es lo que se traduce a Retry-After, así que un valor fuera de
        // rango le diría al cliente que espere más de lo que la regla exige.
        for (var s = -60; s <= 1000; s++)
        {
            var falta = CooldownWindow.Remaining(s, Quince);
            if (falta is null) continue;
            Assert.InRange(falta.Value, TimeSpan.Zero, Quince);
        }
    }
}
