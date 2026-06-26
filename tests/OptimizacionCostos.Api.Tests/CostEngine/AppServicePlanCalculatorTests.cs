using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Port de los tests de AppServicePlanCalculator de
/// tests/test_paas_pricing.py::PaaSCalculatorTests (los que ejercitan la calculadora
/// con precios MOCKEADOS, no los de selección de precios del price_repository).
///
/// Montos esperados copiados EXACTAMENTE del test Python:
///   - payg_monthly P1v3 cap2 = 0.20 * 730 * 2 = 292.0
///   - ri_1y_monthly          = (1200 / 12) * 2 = 200.0
///   - ri_3y_monthly          = (2700 / 36) * 2 = 150.0
///   - EP1 cap2 payg          = 0.20 * 730 * 2 = 292.0
///   - WS1 cap1 payg          = 0.24 * 730 * 1 = 175.2
/// </summary>
public sealed class AppServicePlanCalculatorTests
{
    private const double HoursPerMonth = 730.0;

    private static double MonthlyFromHourly(double price, double hours = HoursPerMonth) => price * hours;

    private static double MonthlyFrom1y(double total) => total / 12.0;

    private static double MonthlyFrom3y(double total) => total / 36.0;

    private static AppServicePlanCalculator Calculator(IPriceRepository prices) =>
        new(prices, new FakePricingConstants());

    // test_app_service_calculator_multiplies_capacity_and_ri
    [Fact]
    public void MultipliesCapacityAndRi()
    {
        const double paygHourly = 0.20;
        const double ri1yTotal = 1200.0;
        const double ri3yTotal = 2700.0;
        const int capacity = 2;

        var prices = new FakePriceRepository
        {
            GetAppServicePricesFn = (_, _, _) => new AppServicePrices(paygHourly, ri1yTotal, ri3yTotal),
        };

        var resource = Res.Row(
            ("resource_id", 1),
            ("sku_name", "P1v3"),
            ("sku_capacity", capacity),
            ("is_linux", true),
            ("location", "East US"));

        var result = Calculator(prices).Calculate(Res.Rows(resource), 99)[0];

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(MonthlyFromHourly(paygHourly) * capacity, result.PaygMonthly!.Value, 5);
        Assert.Equal(MonthlyFrom1y(ri1yTotal) * capacity, result.Ri1yMonthly!.Value, 5);
        Assert.Equal(MonthlyFrom3y(ri3yTotal) * capacity, result.Ri3yMonthly!.Value, 5);
        Assert.True(result.RiApplies);
        Assert.Contains("capacity=2", result.CalculationNotes);
    }

    // test_app_service_calculator_costs_ep_ws_fixed_base_not_variable
    [Fact]
    public void CostsEpWsFixedBaseNotVariable()
    {
        // side_effect: EP* y WS* devuelven bases distintas.
        ElasticPremiumBase BasePrice(string skuName, string region)
            => skuName.ToUpperInvariant().StartsWith("EP", StringComparison.Ordinal)
                ? new ElasticPremiumBase(BaseHourly: 0.20, VcpuHourly: 0.16, MemGibHourly: 0.0114, Vcpu: 1, Gb: 3.5)
                : new ElasticPremiumBase(BaseHourly: 0.24, VcpuHourly: 0.192, MemGibHourly: 0.0137, Vcpu: 1, Gb: 3.5);

        var getAppServiceCalled = false;
        var prices = new FakePriceRepository
        {
            GetElasticPremiumBasePriceFn = BasePrice,
            GetAppServicePricesFn = (_, _, _) =>
            {
                getAppServiceCalled = true; // equivale a get_prices.assert_not_called()
                return new AppServicePrices(null, null, null);
            },
        };

        var resources = Res.Rows(
            Res.Row(("resource_id", 10), ("sku_name", "EP1"), ("sku_capacity", 2), ("is_linux", false), ("location", "eastus2")),
            Res.Row(("resource_id", 11), ("sku_name", "WS1"), ("sku_capacity", 1), ("is_linux", false), ("location", "eastus")));

        var calculated = Calculator(prices).Calculate(resources, 99);
        var ep = calculated[0];
        var ws = calculated[1];

        Assert.False(getAppServiceCalled);

        Assert.Equal("calculated", ep.CalculationStatus);
        Assert.Equal(0.20 * 730.0 * 2, ep.PaygMonthly!.Value, 5);
        Assert.False(ep.RiApplies);
        Assert.False(ep.IsVariablePricing);
        Assert.Contains("excedente variable", ep.CalculationNotes);

        Assert.Equal("calculated", ws.CalculationStatus);
        Assert.Equal(0.24 * 730.0 * 1, ws.PaygMonthly!.Value, 5);
    }

    // test_app_service_calculator_ep_without_prices_is_manual
    [Fact]
    public void EpWithoutPricesIsManual()
    {
        var prices = new FakePriceRepository
        {
            GetElasticPremiumBasePriceFn = (_, _) => null,
        };

        var resource = Res.Row(
            ("resource_id", 12),
            ("sku_name", "EP2"),
            ("sku_capacity", 1),
            ("is_linux", false),
            ("location", "westus"));

        var result = Calculator(prices).Calculate(Res.Rows(resource), 99)[0];

        Assert.Equal("manual_required", result.CalculationStatus);
        Assert.Null(result.PaygMonthly);
    }
}
