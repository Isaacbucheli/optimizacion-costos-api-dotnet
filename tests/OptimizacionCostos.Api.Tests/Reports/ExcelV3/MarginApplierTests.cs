using OptimizacionCostos.Api.Features.Reports.ExcelV3;
namespace OptimizacionCostos.Api.Tests.Reports.ExcelV3;
public class MarginApplierTests
{
    [Fact]
    public void Multiplica_solo_claves_monetarias_presentes()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        { ["payg_monthly"] = 100.0, ["ri_1y_monthly"] = 60.0, ["savings_1y_pct"] = 0.4, ["resource_name"] = "x", ["ri_3y_monthly"] = null };
        MarginApplier.ApplyInPlace(new[] { row }, 10m);
        Assert.Equal(110.0, (double)row["payg_monthly"]!, 2);
        Assert.Equal(66.0, (double)row["ri_1y_monthly"]!, 2);
        Assert.Equal(0.4, (double)row["savings_1y_pct"]!, 4); // % intacto
        Assert.Null(row["ri_3y_monthly"]);
        Assert.Equal("x", row["resource_name"]);
    }

    [Fact]
    public void Porcentajes_de_ahorro_invariantes_bajo_margen()
    {
        // (payg*k - ri*k) / (payg*k) == (payg - ri) / payg
        var payg = MarginApplier.Apply(430, 12m); var total = MarginApplier.Apply(350, 12m);
        Assert.Equal(80.0 / 430.0, (payg - total) / payg, 6);
    }
}
