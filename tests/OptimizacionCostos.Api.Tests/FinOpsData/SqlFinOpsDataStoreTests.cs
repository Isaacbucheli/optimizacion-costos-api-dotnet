using OptimizacionCostos.Api.Features.FinOpsData;
using Xunit;

namespace OptimizacionCostos.Api.Tests.FinOpsData;

public sealed class SqlFinOpsDataStoreTests
{
    private static string[] Row(string unit) => new[] { unit, "EA", "1", "Units" };

    [Fact]
    public void DedupePricingUnits_ColisionesDeColacion_UnaFilaPorClave()
    {
        // CI_AS: case-insensitive + ignora espacios FINALES. Estos son los casos reales del open data.
        var rows = new List<string[]>
        {
            Row("1"), Row("1 "),          // espacio final
            Row("1 GB"), Row("1 Gb"),     // mayúsculas
            Row("1 Hour"), Row("1 hour"),
            Row("2"),                      // sin colisión
        };

        var deduped = SqlFinOpsDataStore.DedupePricingUnits(rows);

        // claves distintas: "1", "1 gb", "1 hour", "2"
        Assert.Equal(4, deduped.Count);
        Assert.Single(deduped, r => r[0] == "1");           // conserva la primera aparición
        Assert.DoesNotContain(deduped, r => r[0] == "1 ");
        Assert.DoesNotContain(deduped, r => r[0] == "1 Gb");
    }

    [Fact]
    public void DedupePricingUnits_EspacioInicialEsSignificativo_NoColisiona()
    {
        // SQL SÍ distingue espacios INICIALES: ' 1' y '1' son claves distintas.
        var deduped = SqlFinOpsDataStore.DedupePricingUnits(new List<string[]> { Row(" 1"), Row("1") });
        Assert.Equal(2, deduped.Count);
    }

    [Fact]
    public void DedupePricingUnits_SinColisiones_DevuelveTodas()
    {
        var rows = new List<string[]> { Row("1 Day"), Row("1 Month"), Row("100 /Hour") };
        Assert.Equal(3, SqlFinOpsDataStore.DedupePricingUnits(rows).Count);
    }
}
