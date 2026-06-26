using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Port 1:1 de los tests de CosmosAccountCalculator en
/// optimizacion-costos-api-github/tests/test_paas_pricing.py.
///
/// Cosmos no consulta precios ni constantes, pero igual se inyectan los dobles de prueba
/// por uniformidad de DI. Los montos/estados aseverados son EXACTAMENTE los del test Python.
/// </summary>
public sealed class CosmosAccountCalculatorTests
{
    private static CosmosAccountCalculator NewCalculator()
        => new(new FakePriceRepository(), new FakePricingConstants());

    /// <summary>
    /// Port de test_cosmos_does_not_invent_minimum_ru_costs:
    /// serverless => variable_pricing (payg NULL); provisioned => manual_required
    /// y NO marca is_manual_cost. Nunca inventa un costo mínimo de RU.
    /// </summary>
    [Fact]
    public void Cosmos_DoesNotInventMinimumRuCosts()
    {
        var serverless = Res.Row(
            ("resource_id", 1),
            ("is_serverless", true),
            ("api_kind", "SQL"),
            ("region_count", 1));

        var provisioned = Res.Row(
            ("resource_id", 2),
            ("is_serverless", false),
            ("api_kind", "SQL"),
            ("region_count", 1),
            ("multi_region_writes", false));

        var results = NewCalculator().Calculate(Res.Rows(serverless, provisioned), 99);
        var first = results[0];
        var second = results[1];

        Assert.Equal("variable_pricing", first.CalculationStatus);
        Assert.True(first.IsVariablePricing);
        Assert.Null(first.PaygMonthly);
        Assert.Equal("manual_required", second.CalculationStatus);
        Assert.False(second.IsManualCost);
    }
}
