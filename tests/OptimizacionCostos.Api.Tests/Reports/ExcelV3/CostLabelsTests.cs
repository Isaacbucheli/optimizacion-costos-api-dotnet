using OptimizacionCostos.Api.Features.Reports.ExcelV3;
namespace OptimizacionCostos.Api.Tests.Reports.ExcelV3;
public class CostLabelsTests
{
    private static Dictionary<string, object?> Row(params (string k, object? v)[] kv)
    { var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase); foreach (var (k, v) in kv) d[k] = v; return d; }

    [Theory]
    [InlineData("calculated", "Calculado")]
    [InlineData("variable_pricing", "Precio variable")]
    [InlineData("manual_required", "Requiere costo manual")]
    [InlineData("not_running", "No encendida")]
    [InlineData(null, "")]
    public void Estados_en_espanol(string? raw, string expected) => Assert.Equal(expected, CostLabels.StatusEs(raw));

    [Fact]
    public void Nota_tecnica_cruda_no_se_expone()
    {
        var r = Row(("calculation_notes", "sku=B1 region=eastus2 linux=True capacity=1"));
        Assert.DoesNotContain("sku=", CostLabels.NoteEs(r));
    }

    [Fact]
    public void Origen_manual_gana()
    {
        var r = Row(("is_manual_cost", true), ("calculation_status", "calculated"));
        Assert.Equal("Manual", CostLabels.PriceOrigin(r));
    }

    [Fact]
    public void Reservado_con_nombre_y_termino()
    {
        var r = Row(("ri_coverage", "confirmed"), ("ri_reservation_name", "res-sql"), ("ri_term", "P3Y"));
        Assert.Equal("Reservado · res-sql (P3Y)", CostLabels.ReservedLabel(r));
    }
}
