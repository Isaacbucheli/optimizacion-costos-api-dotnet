using System.Text.Json;
using OptimizacionCostos.Api.Configuration;
using Xunit;

namespace OptimizacionCostos.Api.Tests;

/// <summary>
/// Los timestamps salen de SQL con Kind=Unspecified y son UTC (la app persiste DateTime.UtcNow).
/// Sin convertidor, System.Text.Json los emitía sin zona ("2026-07-31T15:29:40") y el navegador
/// interpretaba la hora UTC como local. El convertidor debe marcar todo DateTime saliente como
/// UTC con sufijo 'Z'.
/// </summary>
public sealed class UtcDateTimeJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new UtcDateTimeJsonConverter() },
    };

    [Fact]
    public void Unspecified_SaleConSufijoZ()
    {
        var value = new DateTime(2026, 7, 31, 15, 29, 40, DateTimeKind.Unspecified);
        var json = JsonSerializer.Serialize(value, Options);
        Assert.Equal("\"2026-07-31T15:29:40Z\"", json);
    }

    [Fact]
    public void Utc_SaleConSufijoZ()
    {
        var value = new DateTime(2026, 7, 31, 15, 29, 40, DateTimeKind.Utc);
        var json = JsonSerializer.Serialize(value, Options);
        Assert.Equal("\"2026-07-31T15:29:40Z\"", json);
    }

    [Fact]
    public void Nullable_Null_SaleNull()
    {
        DateTime? value = null;
        var json = JsonSerializer.Serialize(value, Options);
        Assert.Equal("null", json);
    }

    [Fact]
    public void Nullable_ConValor_UsaElConvertidor()
    {
        DateTime? value = new DateTime(2026, 7, 31, 15, 29, 40, DateTimeKind.Unspecified);
        var json = JsonSerializer.Serialize(value, Options);
        Assert.Equal("\"2026-07-31T15:29:40Z\"", json);
    }

    [Fact]
    public void Read_ParseaIsoConZona()
    {
        var value = JsonSerializer.Deserialize<DateTime>("\"2026-07-31T15:29:40Z\"", Options);
        Assert.Equal(new DateTime(2026, 7, 31, 15, 29, 40, DateTimeKind.Utc), value.ToUniversalTime());
    }
}
