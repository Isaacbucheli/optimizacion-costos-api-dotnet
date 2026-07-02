using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Port a xUnit de los tests de la calculadora SQL Managed Instance con precios
/// MOCKEADOS (de tests/test_paas_pricing.py, clase SqlManagedInstanceCalculatorTests).
/// Los montos esperados son EXACTAMENTE los del test Python (copiados, no recalculados).
/// </summary>
public sealed class SqlManagedInstanceCalculatorTests
{
    // Constantes/auxiliares espejo de las del test Python.
    private const double HoursPerMonth = 730.0;

    private static double MonthlyFromHourly(double price, double hours = HoursPerMonth) => price * hours;
    private static double MonthlyFrom1y(double total) => total / 12.0;

    /// <summary>
    /// Port de test_sql_managed_instance_calculator_adds_storage_and_ri:
    /// compute por vCore + storage, RI 1y/3y con storage sumado (no descontado).
    /// </summary>
    [Fact]
    public void Calculator_AddsStorageAndRi()
    {
        const double hours = HoursPerMonth;
        const double paygHourlyPerVcore = 0.15;
        const double storageMonthlyPerGb = 0.10;
        const double ri1yTotalPerVcore = 800.0;
        const double ri3yTotalPerVcore = 1800.0;
        const int vcores = 4;
        const int storageGb = 256;

        // Captura de argumentos del lookup (equivalente a assert_called_once_with).
        var callCount = 0;
        (string region, string skuTier, string skuFamily, int vcores, bool zoneRedundant) capturedArgs = default;

        var prices = new FakePriceRepository
        {
            GetSqlManagedInstancePricesFn = (region, skuTier, skuFamily, v, zr) =>
            {
                callCount++;
                capturedArgs = (region, skuTier, skuFamily, v, zr);
                return new SqlManagedInstancePrices(
                    PaygHourlyPerVcore: paygHourlyPerVcore,
                    StorageMonthlyPerGb: storageMonthlyPerGb,
                    Ri1yTotalPerVcore: ri1yTotalPerVcore,
                    Ri3yTotalPerVcore: ri3yTotalPerVcore,
                    ComputeMeterName: "vCore",
                    StorageMeterName: "Data Stored",
                    PaygMeterId: "meter-sqlmi-1");
            }
        };
        var constants = new FakePricingConstants { HoursPerMonthValue = hours };

        var resource = Res.Row(
            ("resource_id", 6),
            ("sku_tier", "GeneralPurpose"),
            ("sku_family", "Gen5"),
            ("vcore_count", vcores),
            ("storage_size_gb", storageGb),
            ("license_type", "LicenseIncluded"),
            ("zone_redundant", true),
            ("location", "eastus2"));

        var result = new SqlManagedInstanceCalculator(prices, constants)
            .Calculate(Res.Rows(resource), 99)[0];

        // get_price.assert_called_once_with("eastus2", "GeneralPurpose", "Gen5", vcores, True)
        Assert.Equal(1, callCount);
        Assert.Equal(("eastus2", "GeneralPurpose", "Gen5", vcores, true), capturedArgs);

        Assert.Equal("calculated", result.CalculationStatus);

        var storageMonthly = storageMonthlyPerGb * storageGb;
        Assert.Equal(MonthlyFromHourly(paygHourlyPerVcore, hours) * vcores + storageMonthly,
            result.PaygMonthly!.Value, 6);
        Assert.Equal(storageMonthly, result.StorageMonthly!.Value, 6);
        Assert.Equal(MonthlyFrom1y(ri1yTotalPerVcore) * vcores + storageMonthly,
            result.Ri1yMonthly!.Value, 6);
        Assert.True(result.RiApplies);
        Assert.Equal("meter-sqlmi-1", result.PaygMeterId);
    }
}
