using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Tests de RedisCacheCalculator, portados 1:1 de
/// tests/test_paas_pricing.py (test_redis_calculator_disables_ri_for_basic_and_enables_for_standard)
/// mas cobertura de las ramas documentadas (manual_required, Enterprise con RI,
/// price_not_found por campos faltantes, descarte de RI no beneficiosa).
///
/// Constantes y helpers iguales a los del test Python:
///   HOURS_PER_MONTH = 730.0
///   monthly_from_hourly(price, hours) = price * hours
///   monthly_from_1y(total) = total / 12.0
///   monthly_from_3y(total) = total / 36.0
/// </summary>
public sealed class RedisCacheCalculatorTests
{
    private const double HoursPerMonth = 730.0;

    private static double MonthlyFromHourly(double price, double hours = HoursPerMonth) => price * hours;
    private static double MonthlyFrom1y(double total) => total / 12.0;
    private static double MonthlyFrom3y(double total) => total / 36.0;

    /// <summary>
    /// Port directo de test_redis_calculator_disables_ri_for_basic_and_enables_for_standard.
    /// Basic (Classic) NO aplica RI; Standard SI. side_effect segun sku_name.
    /// </summary>
    [Fact]
    public void DisablesRiForBasicAndEnablesForStandard()
    {
        const double basicHourly = 0.01;
        const double standardHourly = 0.10;
        const double standardRi1y = 500.0;
        const double standardRi3y = 1200.0;

        var resources = Res.Rows(
            Res.Row(
                ("resource_id", 3),
                ("sku_name", "Basic"),
                ("sku_family", "C"),
                ("sku_capacity", 0),
                ("location", "eastus")),
            Res.Row(
                ("resource_id", 4),
                ("sku_name", "Standard"),
                ("sku_family", "C"),
                ("sku_capacity", 1),
                ("location", "eastus")));

        var prices = new FakePriceRepository
        {
            // side_effect=price_data: Basic sin RI; resto con RI.
            GetRedisPricesFn = (skuName, _, _) => skuName == "Basic"
                ? new RedisPrices(basicHourly, null, null)
                : new RedisPrices(standardHourly, standardRi1y, standardRi3y, PaygMeterId: "meter-redis-1"),
        };
        var constants = new FakePricingConstants();

        var output = new RedisCacheCalculator(prices, constants).Calculate(resources, 99);
        var basic = output[0];
        var standard = output[1];

        Assert.Equal(MonthlyFromHourly(basicHourly, HoursPerMonth), basic.PaygMonthly!.Value, 6);
        Assert.False(basic.RiApplies);
        Assert.Null(basic.Ri1yMonthly);

        Assert.Equal(MonthlyFromHourly(standardHourly, HoursPerMonth), standard.PaygMonthly!.Value, 6);
        Assert.True(standard.RiApplies);
        Assert.Equal(MonthlyFrom1y(standardRi1y), standard.Ri1yMonthly!.Value, 6);
        Assert.Equal("meter-redis-1", standard.PaygMeterId);
        Assert.Contains("capacity=1", standard.CalculationNotes);
    }

    /// <summary>
    /// Premium (Classic) tambien aplica RI (no es Basic). Verifica RI 1y y 3y.
    /// </summary>
    [Fact]
    public void EnablesRiForPremium()
    {
        const double premiumHourly = 0.50;
        const double ri1y = 3000.0;
        const double ri3y = 7000.0;

        var resources = Res.Rows(
            Res.Row(
                ("resource_id", 10),
                ("sku_name", "Premium"),
                ("sku_family", "P"),
                ("sku_capacity", 1),
                ("location", "eastus")));

        var prices = new FakePriceRepository
        {
            GetRedisPricesFn = (_, _, _) => new RedisPrices(premiumHourly, ri1y, ri3y, "deterministic"),
        };

        var result = new RedisCacheCalculator(prices, new FakePricingConstants()).Calculate(resources, 99)[0];

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.Equal(MonthlyFromHourly(premiumHourly), result.PaygMonthly!.Value, 6);
        Assert.True(result.RiApplies);
        Assert.Equal(MonthlyFrom1y(ri1y), result.Ri1yMonthly!.Value, 6);
        Assert.Equal(MonthlyFrom3y(ri3y), result.Ri3yMonthly!.Value, 6);
    }

    /// <summary>
    /// Enterprise (Managed Redis) aplica RI aunque el nombre contenga "Basic"
    /// (la regla excluye solo si contiene "basic" y NO contiene "enterprise").
    /// </summary>
    [Fact]
    public void EnablesRiForEnterpriseEvenWhenNameContainsBasic()
    {
        const double hourly = 1.00;
        const double ri1y = 6000.0;
        const double ri3y = 15000.0;

        var resources = Res.Rows(
            Res.Row(
                ("resource_id", 11),
                ("sku_name", "Enterprise_Basic_E10"),
                ("sku_family", "E"),
                ("sku_capacity", 2),
                ("location", "eastus")));

        var prices = new FakePriceRepository
        {
            GetRedisPricesFn = (_, _, _) => new RedisPrices(hourly, ri1y, ri3y),
        };

        var result = new RedisCacheCalculator(prices, new FakePricingConstants()).Calculate(resources, 99)[0];

        Assert.True(result.RiApplies);
        Assert.Equal(MonthlyFrom1y(ri1y), result.Ri1yMonthly!.Value, 6);
        Assert.Equal(MonthlyFrom3y(ri3y), result.Ri3yMonthly!.Value, 6);
    }

    /// <summary>
    /// payg_hourly null del repo => manual_required, sin costo.
    /// </summary>
    [Fact]
    public void ManualRequiredWhenNoPaygHourly()
    {
        var resources = Res.Rows(
            Res.Row(
                ("resource_id", 12),
                ("sku_name", "Standard"),
                ("sku_family", "C"),
                ("sku_capacity", 1),
                ("location", "eastus")));

        var prices = new FakePriceRepository
        {
            GetRedisPricesFn = (_, _, _) => new RedisPrices(null, null, null),
        };

        var result = new RedisCacheCalculator(prices, new FakePricingConstants()).Calculate(resources, 99)[0];

        Assert.Equal("manual_required", result.CalculationStatus);
        Assert.Null(result.PaygMonthly);
        Assert.Contains("Requiere validacion manual", result.CalculationNotes);
    }

    /// <summary>
    /// Sin sku_name o sin location => price_not_found "Missing sku_name or location".
    /// </summary>
    [Fact]
    public void PriceNotFoundWhenMissingSkuOrLocation()
    {
        var resources = Res.Rows(
            Res.Row(
                ("resource_id", 13),
                ("sku_name", "Standard"),
                ("sku_family", "C"),
                ("sku_capacity", 1),
                ("location", "")));

        var result = new RedisCacheCalculator(new FakePriceRepository(), new FakePricingConstants())
            .Calculate(resources, 99)[0];

        Assert.Equal("price_not_found", result.CalculationStatus);
        Assert.Equal("Missing sku_name or location", result.CalculationNotes);
        Assert.Null(result.PaygMonthly);
    }

    /// <summary>
    /// RI &gt;= PAYG => DiscardNonSavingRi la elimina y desactiva ri_applies.
    /// </summary>
    [Fact]
    public void DiscardsRiWhenNotBeneficial()
    {
        const double hourly = 0.10; // payg_monthly = 0.10 * 730 = 73
        // RI total muy alto: ri_1y = 1200/12 = 100 > 73 ; ri_3y = 3600/36 = 100 > 73
        const double ri1y = 1200.0;
        const double ri3y = 3600.0;

        var resources = Res.Rows(
            Res.Row(
                ("resource_id", 14),
                ("sku_name", "Standard"),
                ("sku_family", "C"),
                ("sku_capacity", 1),
                ("location", "eastus")));

        var prices = new FakePriceRepository
        {
            GetRedisPricesFn = (_, _, _) => new RedisPrices(hourly, ri1y, ri3y),
        };

        var result = new RedisCacheCalculator(prices, new FakePricingConstants()).Calculate(resources, 99)[0];

        Assert.Equal(MonthlyFromHourly(hourly), result.PaygMonthly!.Value, 6);
        Assert.Null(result.Ri1yMonthly);
        Assert.Null(result.Ri3yMonthly);
        Assert.False(result.RiApplies);
    }
}
