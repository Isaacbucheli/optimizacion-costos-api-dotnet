using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Port de tests/test_paas_pricing.py::ElasticPoolCalculatorTests (solo los que ejercitan
/// la calculadora con precios mockeados). Montos esperados copiados EXACTAMENTE del Python.
///
/// El test Python test_sql_db_in_elastic_pool_costs_zero ejercita SqlDatabaseCalculator,
/// no ElasticPoolCalculator, así que se porta junto a esa otra calculadora.
/// </summary>
public sealed class ElasticPoolCalculatorTests
{
    // Python: patch hours_per_month -> 730.0; aquí 730.0 ya es el default de FakePricingConstants.
    private static ElasticPoolCalculator MakeCalculator(IPriceRepository prices)
        => new(prices, new FakePricingConstants());

    [Fact]
    public void Calculator_PricesVcorePool_Monthly()
    {
        var resource = Res.Row(
            ("resource_id", 20),
            ("sku_tier", "GeneralPurpose"),
            ("sku_family", "Gen5"),
            ("capacity", 8),
            ("location", "East US 2"));

        // Capturamos los argumentos del lookup para verificar la normalización de región.
        string? capTier = null, capFamily = null, capRegion = null;
        int capCapacity = 0;
        int callCount = 0;
        var prices = new FakePriceRepository
        {
            GetElasticPoolPricesFn = (tier, family, capacity, region) =>
            {
                callCount++;
                capTier = tier;
                capFamily = family;
                capCapacity = capacity;
                capRegion = region;
                return new ElasticPoolPrices(
                    PaygHourly: 1.40, Model: "vCore", MeterName: "8 vCore", MatchedSku: "8 vCore");
            },
        };

        var result = MakeCalculator(prices).Calculate(Res.Rows(resource), 99)[0];

        // lookup.assert_called_once_with("GeneralPurpose", "Gen5", 8, "eastus2")
        Assert.Equal(1, callCount);
        Assert.Equal("GeneralPurpose", capTier);
        Assert.Equal("Gen5", capFamily);
        Assert.Equal(8, capCapacity);
        Assert.Equal("eastus2", capRegion);

        Assert.Equal("elastic_pool", result.ServiceKey);
        Assert.Equal("calculated", result.CalculationStatus);
        Assert.NotNull(result.PaygMonthly);
        Assert.Equal(1.40 * 730.0, result.PaygMonthly!.Value, 5); // payg_monthly == 1.40 * 730.0
        Assert.False(result.RiApplies);
    }

    [Fact]
    public void Calculator_WithoutExactPrice_IsPriceNotFound_NotEstimated()
    {
        var resource = Res.Row(
            ("resource_id", 21),
            ("sku_tier", "GeneralPurpose"),
            ("sku_family", "Gen5"),
            ("capacity", 6),
            ("location", "East US 2"));

        // get_elastic_pool_prices -> None (sin match exacto): no se aproxima.
        var prices = new FakePriceRepository
        {
            GetElasticPoolPricesFn = (_, _, _, _) => null,
        };

        var result = MakeCalculator(prices).Calculate(Res.Rows(resource), 99)[0];

        Assert.Equal("price_not_found", result.CalculationStatus);
        Assert.Null(result.PaygMonthly);
        Assert.NotNull(result.CalculationNotes);
        Assert.Contains("no se aproxima", result.CalculationNotes!.ToLowerInvariant());
    }
}
