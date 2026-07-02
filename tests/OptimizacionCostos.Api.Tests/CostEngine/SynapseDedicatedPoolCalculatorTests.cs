using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Port de SynapseDwPricingTests (tests del calculador) de tests/test_paas_pricing.py.
/// Solo se portan los tests que ejercitan SynapseDedicatedPoolCalculator con precios
/// mockeados; los que prueban prices._select_synapse_dw_prices (selección de precios del
/// repositorio) pertenecen al port del price_repository, no a la calculadora.
///
/// Los tests Python parchean constants.hours_per_month a 730; aquí 730 ya es el default
/// de FakePricingConstants, así que basta con new FakePricingConstants().
/// </summary>
public sealed class SynapseDedicatedPoolCalculatorTests
{
    private static SynapseDedicatedPoolCalculator NewCalculator(
        FakePriceRepository prices, FakePricingConstants? constants = null)
        => new(prices, constants ?? new FakePricingConstants());

    // test_calculator_prices_online_pool_from_properties_level
    [Fact]
    public void Calculator_PricesOnlinePool_FromPropertiesLevel()
    {
        var resource = Res.Row(
            ("resource_id", 1),
            ("sku_name", "DataWarehouse"),
            ("sku_tier", "DataWarehouse"),
            ("sku_capacity", 900),    // ARM reporta 900 aunque el pool es DW100c
            ("location", "East US 2"),
            ("properties_json", "{\"status\": \"Online\", \"currentServiceObjectiveName\": \"DW100c\"}"));

        // Captura los argumentos del lookup (equivale a lookup.assert_called_once_with).
        string? capturedLevel = null;
        string? capturedRegion = null;
        var callCount = 0;
        var prices = new FakePriceRepository
        {
            GetSynapseDwPricesFn = (level, region) =>
            {
                callCount++;
                capturedLevel = level;
                capturedRegion = region;
                return new SynapseDwPrices(
                    PaygHourly: 1.20,
                    Ri1yTotal: 6623.0,
                    Ri3yTotal: 11038.0,
                    PaygMeterId: "meter-synapse-1");
            }
        };

        var result = NewCalculator(prices).Calculate(Res.Rows(resource), 99)[0];

        // lookup.assert_called_once_with("DW100c", "eastus2")
        Assert.Equal(1, callCount);
        Assert.Equal("DW100c", capturedLevel);
        Assert.Equal("eastus2", capturedRegion);

        Assert.Equal("synapse_dw", result.ServiceKey);
        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(1.20 * 730.0, result.PaygMonthly!.Value, 5);
        Assert.Equal(6623.0 / 12.0, result.Ri1yMonthly!.Value, 5);
        Assert.Equal(11038.0 / 36.0, result.Ri3yMonthly!.Value, 5);
        Assert.True(result.RiApplies);
        Assert.Equal("meter-synapse-1", result.PaygMeterId);
    }

    // test_calculator_paused_pool_costs_zero_compute
    [Fact]
    public void Calculator_PausedPool_CostsZeroCompute()
    {
        var resource = Res.Row(
            ("resource_id", 1),
            ("sku_name", "DataWarehouse"),
            ("sku_tier", "DataWarehouse"),
            ("sku_capacity", 900),
            ("location", "East US 2"),
            ("properties_json", "{\"status\": \"Paused\", \"currentServiceObjectiveName\": \"DW100c\"}"));

        // Pausado corta antes del lookup de precios: repositorio por defecto.
        var prices = new FakePriceRepository();

        var result = NewCalculator(prices).Calculate(Res.Rows(resource), 99)[0];

        Assert.Equal("not_running", result.CalculationStatus);
        Assert.Equal(0.0, result.PaygMonthly!.Value, 5);
        Assert.False(result.RiApplies);
    }

    // test_sql_calculator_skips_datawarehouse_and_synapse_skips_others
    // (solo la mitad de Synapse: el calculador procesa la fila DataWarehouse y omite la SQL regular)
    [Fact]
    public void Calculator_SkipsNonDataWarehouseRows()
    {
        var dw = Res.Row(
            ("resource_id", 1),
            ("sku_name", "DataWarehouse"),
            ("sku_tier", "DataWarehouse"),
            ("location", "East US 2"),
            ("properties_json", "{\"status\": \"Paused\", \"currentServiceObjectiveName\": \"DW100c\"}"));
        var regular = Res.Row(
            ("resource_id", 2),
            ("sku_name", "GP_S_Gen5"),
            ("sku_tier", "GeneralPurpose"),
            ("location", "East US 2"));

        var prices = new FakePriceRepository();

        var synapseResults = NewCalculator(prices).Calculate(Res.Rows(dw, regular), 99);

        // self.assertEqual([r.resource_id for r in synapse_results], [1])
        Assert.Equal(new[] { 1 }, synapseResults.Select(r => r.ResourceId).ToArray());
    }

    // Paridad con el `or` (falsy) de Python: si currentServiceObjectiveName viene como
    // cadena VACÍA, debe caer al fallback requestedServiceObjectiveName y costear igual.
    // (Caso borde detectado por la verificación adversarial; el `??` antiguo lo rompía.)
    [Fact]
    public void Calculator_EmptyCurrentObjective_FallsBackToRequested()
    {
        var resource = Res.Row(
            ("resource_id", 1),
            ("sku_name", "DataWarehouse"),
            ("sku_tier", "DataWarehouse"),
            ("location", "East US 2"),
            ("properties_json",
                "{\"status\": \"Online\", \"currentServiceObjectiveName\": \"\", "
                + "\"requestedServiceObjectiveName\": \"DW100c\"}"));

        string? capturedLevel = null;
        var prices = new FakePriceRepository
        {
            GetSynapseDwPricesFn = (level, _) =>
            {
                capturedLevel = level;
                return new SynapseDwPrices(PaygHourly: 1.20, Ri1yTotal: null, Ri3yTotal: null);
            }
        };

        var result = NewCalculator(prices).Calculate(Res.Rows(resource), 99)[0];

        Assert.Equal("DW100c", capturedLevel);
        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(1.20 * 730.0, result.PaygMonthly!.Value, 5);
    }
}
