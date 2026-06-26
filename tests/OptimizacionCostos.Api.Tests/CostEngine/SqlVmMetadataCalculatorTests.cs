using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.CostEngine.Calculators;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Pruebas de SqlVmMetadataCalculator (service_key "sql_vm"), port de
/// app/calculators/sql_vm_metadata.py.
///
/// El origen Python no tiene un test dedicado con precios mockeados (la calculadora
/// no consulta precios: solo marca metadata). Estos tests aseveran la PARIDAD EXACTA
/// del comportamiento: por cada recurso de entrada se emite un CostResult
/// not_applicable, con la nota fija, sin costo ni RI.
/// </summary>
public sealed class SqlVmMetadataCalculatorTests
{
    private const string ExpectedNote = "SQL VM es metadata. El costo está sumado en la VM asociada.";

    private static SqlVmMetadataCalculator NewCalculator()
        // Recibe repo/constantes por consistencia de firma aunque no los use.
        => new(new FakePriceRepository(), new FakePricingConstants());

    [Fact]
    public void Marca_cada_recurso_como_not_applicable()
    {
        var calc = NewCalculator();
        var resources = Res.Rows(
            Res.Row(("resource_id", 1), ("resource_name", "sqlvm-prod")),
            Res.Row(("resource_id", 2), ("resource_name", "sqlvm-dev")));

        var results = calc.Calculate(resources, analysisId: 99);

        // 1:1 con la entrada (no filtra nada).
        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal("sql_vm", r.ServiceKey);
            Assert.Equal("not_applicable", r.CalculationStatus);
            Assert.Equal(ExpectedNote, r.CalculationNotes);
            Assert.Equal(99, r.AnalysisId);
        });
    }

    [Fact]
    public void Preserva_el_resource_id_y_orden_de_entrada()
    {
        var calc = NewCalculator();
        var resources = Res.Rows(
            Res.Row(("resource_id", 10)),
            Res.Row(("resource_id", 20)),
            Res.Row(("resource_id", 30)));

        var results = calc.Calculate(resources, analysisId: 7);

        Assert.Equal(new[] { 10, 20, 30 }, results.Select(r => r.ResourceId).ToArray());
    }

    [Fact]
    public void No_costea_nada_payg_ri_y_storage_quedan_null()
    {
        var calc = NewCalculator();
        var resources = Res.Rows(Res.Row(("resource_id", 1)));

        var result = Assert.Single(calc.Calculate(resources, analysisId: 1));

        Assert.Null(result.PaygHourly);
        Assert.Null(result.PaygMonthly);
        Assert.Null(result.Ri1yMonthly);
        Assert.Null(result.Ri3yMonthly);
        Assert.Null(result.SqlAddonMonthly);
        Assert.Null(result.StorageMonthly);
        Assert.Null(result.Savings1yMonthly);
        Assert.Null(result.Savings3yMonthly);
    }

    [Fact]
    public void No_aplica_ri_ni_pricing_variable_ni_manual()
    {
        var calc = NewCalculator();
        var resources = Res.Rows(Res.Row(("resource_id", 1)));

        var result = Assert.Single(calc.Calculate(resources, analysisId: 1));

        Assert.False(result.RiApplies);
        Assert.False(result.IsVariablePricing);
        Assert.False(result.IsManualCost);
        Assert.Null(result.ManualMonthlyCost);
    }

    [Fact]
    public void Lista_vacia_devuelve_lista_vacia()
    {
        var calc = NewCalculator();

        var results = calc.Calculate(Res.Rows(), analysisId: 5);

        Assert.Empty(results);
    }

    [Fact]
    public void Nunca_consulta_el_repositorio_de_precios()
    {
        // La calculadora no debe tocar precios: si lo hiciera, fallaría la aserción.
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, _) => throw new Xunit.Sdk.XunitException("no debe consultar precios"),
            GetSqlManagedInstancePricesFn = (_, _, _, _, _) => throw new Xunit.Sdk.XunitException("no debe consultar precios"),
        };
        var calc = new SqlVmMetadataCalculator(prices, new FakePricingConstants());

        var result = Assert.Single(calc.Calculate(
            Res.Rows(Res.Row(("resource_id", 1), ("vm_size", "Standard_E4s_v5"))),
            analysisId: 1));

        Assert.Equal("not_applicable", result.CalculationStatus);
    }
}
