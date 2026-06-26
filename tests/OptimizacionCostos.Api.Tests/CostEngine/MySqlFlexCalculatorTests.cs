using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Tests de paridad de <see cref="MySqlFlexCalculator"/> contra
/// app/calculators/mysql_flex.py y tests/test_paas_pricing.py.
/// Los montos esperados son copiados exactamente de los tests Python.
/// </summary>
public sealed class MySqlFlexCalculatorTests
{
    // Constantes equivalentes a las del test Python (HOURS_PER_MONTH=730, default de FakePricingConstants).
    private const double HoursPerMonth = 730.0;

    private static double MonthlyFromHourly(double price, double hours = HoursPerMonth) => price * hours;
    private static double MonthlyFrom1y(double total) => total / 12.0;
    private static double MonthlyFrom3y(double total) => total / 36.0;

    private static MySqlFlexCalculator NewCalc(FakePriceRepository prices, FakePricingConstants? consts = null)
        => new(prices, consts ?? new FakePricingConstants());

    /// <summary>
    /// Port de test_mysql_calculator_adds_storage_and_skips_burstable_ri.
    /// payg_monthly = 0.04*730 + 0.10*128 = 42.0 ; storage_monthly = 12.8 ; sin RI (Burstable).
    /// </summary>
    [Fact]
    public void Calculator_AddsStorage_AndSkipsBurstableRi()
    {
        const double paygHourly = 0.04;
        const double storageMonthlyPerGb = 0.10;
        const int storageGb = 128;

        var prices = new FakePriceRepository
        {
            GetMySqlFlexPricesFn = (_, _, _) => new MySqlFlexPrices(paygHourly, 100.0, 200.0),
            GetMySqlStoragePricePerGbFn = _ => storageMonthlyPerGb,
        };

        var resource = Res.Row(
            ("resource_id", 2),
            ("sku_name", "Standard_B1ms"),
            ("sku_tier", "burstable"),
            ("storage_size_gb", storageGb),
            ("location", "eastus"));

        var result = NewCalc(prices).Calculate(Res.Rows(resource), 99)[0];

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(MonthlyFromHourly(paygHourly) + storageMonthlyPerGb * storageGb, result.PaygMonthly!.Value, 9);
        Assert.Equal(storageMonthlyPerGb * storageGb, result.StorageMonthly!.Value, 9);
        Assert.False(result.RiApplies);
        Assert.Null(result.Ri1yMonthly);
        Assert.Contains("storage_gb=128", result.CalculationNotes);
    }

    /// <summary>
    /// Paridad de la rama no-Burstable (GeneralPurpose): ri_applies y RI mensual = total/12 + storage.
    /// Montos derivados directamente de las fórmulas del .py (no inventados).
    /// </summary>
    [Fact]
    public void Calculator_GeneralPurpose_AppliesRi()
    {
        const double paygHourly = 0.50;
        const double ri1yTotal = 3000.0;
        const double ri3yTotal = 7200.0;
        const double storagePerGb = 0.115;
        const int storageGb = 64;

        var prices = new FakePriceRepository
        {
            GetMySqlFlexPricesFn = (_, _, _) => new MySqlFlexPrices(paygHourly, ri1yTotal, ri3yTotal),
            GetMySqlStoragePricePerGbFn = _ => storagePerGb,
        };

        var resource = Res.Row(
            ("resource_id", 10),
            ("sku_name", "Standard_D2ds_v4"),
            ("sku_tier", "GeneralPurpose"),
            ("storage_size_gb", storageGb),
            ("location", "East US"));

        var result = NewCalc(prices).Calculate(Res.Rows(resource), 99)[0];

        var storageMonthly = storagePerGb * storageGb;
        Assert.Equal("calculated", result.CalculationStatus);
        // is_per_vcore=false => vcores=1.
        Assert.Equal(MonthlyFromHourly(paygHourly) + storageMonthly, result.PaygMonthly!.Value, 9);
        Assert.True(result.RiApplies);
        Assert.Equal(MonthlyFrom1y(ri1yTotal) + storageMonthly, result.Ri1yMonthly!.Value, 9);
        Assert.Equal(MonthlyFrom3y(ri3yTotal) + storageMonthly, result.Ri3yMonthly!.Value, 9);
    }

    /// <summary>
    /// Paridad de la rama is_per_vcore: compute y RI se multiplican por los vCores del SKU
    /// (regex _([A-Za-z]+)?(\d+) -> "Standard_D4ds_v4" => 4).
    /// </summary>
    [Fact]
    public void Calculator_PerVcore_MultipliesByVcores()
    {
        const double paygHourly = 0.10;
        const double ri1yTotal = 600.0;
        const double ri3yTotal = 1440.0;
        const int vcores = 4; // derivado de "Standard_D4ds_v4"

        var prices = new FakePriceRepository
        {
            GetMySqlFlexPricesFn = (_, _, _) => new MySqlFlexPrices(paygHourly, ri1yTotal, ri3yTotal, IsPerVcore: true),
            GetMySqlStoragePricePerGbFn = _ => null, // sin storage
        };

        var resource = Res.Row(
            ("resource_id", 11),
            ("sku_name", "Standard_D4ds_v4"),
            ("sku_tier", "GeneralPurpose"),
            ("storage_size_gb", 0),
            ("location", "eastus"));

        var result = NewCalc(prices).Calculate(Res.Rows(resource), 99)[0];

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(paygHourly * HoursPerMonth * vcores, result.PaygMonthly!.Value, 9);
        Assert.Null(result.StorageMonthly); // storage_monthly==0 => None
        Assert.True(result.RiApplies);
        // storage_monthly=0 => RI = total*vcores/N + 0
        Assert.Equal((ri1yTotal * vcores) / 12.0, result.Ri1yMonthly!.Value, 9);
        Assert.Equal((ri3yTotal * vcores) / 36.0, result.Ri3yMonthly!.Value, 9);
        Assert.Contains("vcores=4", result.CalculationNotes);
    }

    /// <summary>payg_hourly None => manual_required, sin costo.</summary>
    [Fact]
    public void Calculator_NoPaygPrice_MarksManualRequired()
    {
        var prices = new FakePriceRepository
        {
            GetMySqlFlexPricesFn = (_, _, _) => new MySqlFlexPrices(null, null, null),
        };

        var resource = Res.Row(
            ("resource_id", 12),
            ("sku_name", "Standard_B1ms"),
            ("sku_tier", "Burstable"),
            ("location", "eastus"));

        var result = NewCalc(prices).Calculate(Res.Rows(resource), 99)[0];

        Assert.Equal("manual_required", result.CalculationStatus);
        Assert.Null(result.PaygMonthly);
        Assert.Contains("Requiere validacion manual", result.CalculationNotes);
    }

    /// <summary>sku_name o location vacíos => price_not_found.</summary>
    [Fact]
    public void Calculator_MissingSkuOrLocation_MarksPriceNotFound()
    {
        var prices = new FakePriceRepository();

        var resource = Res.Row(
            ("resource_id", 13),
            ("sku_name", "Standard_B1ms"),
            ("location", null)); // sin location

        var result = NewCalc(prices).Calculate(Res.Rows(resource), 99)[0];

        Assert.Equal("price_not_found", result.CalculationStatus);
        Assert.Equal("Missing sku_name or location", result.CalculationNotes);
        Assert.Null(result.PaygMonthly);
    }
}
