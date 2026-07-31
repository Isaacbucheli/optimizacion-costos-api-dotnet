using System.Data;
using OptimizacionCostos.Api.Features.Waf;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Regresión del bug de resolved_findings = 0: la inferencia por defecto de SqlParameter manda
/// los DateTime como datetime (ticks de 1/300 s). Al escribir resolved_at = @now en una columna
/// datetime2 el servidor guarda el tick expandido (p. ej. .2433333), pero la comparación
/// "resolved_at = @now" del conteo evalúa el parámetro como .2430000 y NO matchea, salvo que el
/// redondeo caiga en milisegundos exactos (~1 de cada 3 corridas). Verificado contra la BD real:
/// la corrida 87 del cliente 9 dejó 4 hallazgos con resolved_at = timestamp de la corrida y
/// resolved_findings quedó en 0. Con datetime2 la igualdad es exacta siempre.
/// </summary>
public sealed class WafIngestionDateTimeParamTests
{
    [Fact]
    public void Param_DateTime_ViajaComoDateTime2()
    {
        var now = DateTime.UtcNow;
        var p = SqlWafIngestionStore.Param("@now", now);
        Assert.Equal(SqlDbType.DateTime2, p.SqlDbType);
        Assert.Equal(now, p.Value);
    }

    [Theory]
    [InlineData(5)]
    [InlineData("texto")]
    public void Param_OtrosTipos_MantienenInferencia(object value)
    {
        var p = SqlWafIngestionStore.Param("@x", value);
        Assert.Equal(value, p.Value);
    }

    [Fact]
    public void Param_DbNull_NoRevienta()
    {
        var p = SqlWafIngestionStore.Param("@x", DBNull.Value);
        Assert.Equal(DBNull.Value, p.Value);
    }
}
