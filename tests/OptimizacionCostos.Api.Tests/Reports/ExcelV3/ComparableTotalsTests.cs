using OptimizacionCostos.Api.Features.Reports.ExcelV3;

namespace OptimizacionCostos.Api.Tests.Reports.ExcelV3;

public class ComparableTotalsTests
{
    [Fact]
    public void Caso_BANISI_AppService_totales_no_inflados()
    {
        // 2 planes elegibles a RI + 3 que NO aplican. El bug histórico: TOTAL RI = 60+80=140
        // vs PAYG total 100+120+50+70+90=430 → "ahorro" fantasma de 290. Lo correcto:
        // Total optimizado 1A = (60+80) + (50+70+90) = 350 → ahorro REAL = 80.
        var rows = new List<TotalsInput>
        {
            new(100, 60, 55, false),
            new(120, 80, 70, false),
            new(50, null, null, false),
            new(70, null, null, false),
            new(90, null, null, false),
        };
        var t = ComparableTotalsCalculator.Compute(rows);
        Assert.Equal(430, t.PaygTotal, 2);
        Assert.Equal(2, t.EligibleCount);
        Assert.Equal(3, t.NotEligibleCount);
        Assert.Equal(350, t.Total1, 2);
        Assert.Equal(80, t.Savings1, 2);
        Assert.Equal(80.0 / 430.0, t.Savings1Pct, 4);
        Assert.Equal(335, t.Total3, 2);   // (55+70) + 210
        Assert.Equal(95, t.Savings3, 2);
    }

    [Fact]
    public void Reservado_confirmado_cuenta_como_no_elegible()
    {
        // Reservado neutralizado (RI=PAYG) NO debe aportar "ahorro" ni contarse elegible.
        var rows = new List<TotalsInput> { new(200, 200, 200, true), new(100, 60, 50, false) };
        var t = ComparableTotalsCalculator.Compute(rows);
        Assert.Equal(1, t.EligibleCount);
        Assert.Equal(260, t.Total1, 2);  // 60 + 200
        Assert.Equal(40, t.Savings1, 2);
    }

    [Fact]
    public void Elegible_con_ri1_nulo_usa_payg_en_esa_columna()
    {
        var rows = new List<TotalsInput> { new(100, null, 70, false) }; // elegible por ri3
        var t = ComparableTotalsCalculator.Compute(rows);
        Assert.Equal(1, t.EligibleCount);
        Assert.Equal(100, t.Total1, 2);  // ri1 null → payg
        Assert.Equal(70, t.Total3, 2);
        Assert.Equal(0, t.Savings1, 2);
    }

    [Fact]
    public void Nada_elegible_ahorro_cero()
    {
        var rows = new List<TotalsInput> { new(50, null, null, false), new(30, null, null, false) };
        var t = ComparableTotalsCalculator.Compute(rows);
        Assert.Equal(0, t.EligibleCount);
        Assert.Equal(80, t.Total1, 2);
        Assert.Equal(0, t.Savings1, 2);
        Assert.Equal(0, t.Savings1Pct, 4);
    }

    [Fact]
    public void Lista_vacia_todo_cero_sin_dividir_por_cero()
    {
        var t = ComparableTotalsCalculator.Compute(new List<TotalsInput>());
        Assert.Equal(0, t.PaygTotal);
        Assert.Equal(0, t.Savings1Pct);
    }
}
