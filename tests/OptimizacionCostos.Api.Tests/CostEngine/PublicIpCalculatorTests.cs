using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Tests de <see cref="PublicIpCalculator"/>. Port de
/// tests/test_paas_pricing.py::test_public_ip_calculator_only_returns_orphans
/// (más cobertura de paridad para los caminos manual_required / RI no aplica).
/// Los montos esperados son EXACTAMENTE los del test Python.
/// </summary>
public sealed class PublicIpCalculatorTests
{
    // HOURS_PER_MONTH = 730.0 en el test Python; monthly_from_hourly(p) = p * 730.
    private const double HoursPerMonth = 730.0;

    private static double MonthlyFromHourly(double hourly) => hourly * HoursPerMonth;

    private static PublicIpCalculator NewCalculator(FakePriceRepository prices)
        => new(prices, new FakePricingConstants());

    /// <summary>
    /// Port de test_public_ip_calculator_only_returns_orphans:
    /// solo devuelve la IP huérfana (resource_id 1), con payg_monthly = monthly_price,
    /// y consulta el precio una sola vez.
    /// </summary>
    [Fact]
    public void Calculate_OnlyReturnsOrphans()
    {
        var monthlyPrice = MonthlyFromHourly(0.005); // = 3.65

        var callCount = 0;
        var prices = new FakePriceRepository
        {
            GetPublicIpMonthlyPriceFn = (_, _, _) =>
            {
                callCount++;
                return monthlyPrice;
            },
        };

        var resources = Res.Rows(
            Res.Row(
                ("resource_id", 1),
                ("sku_name", "Standard"),
                ("allocation_method", "Static"),
                ("location", "eastus2"),
                ("is_orphan", true)),
            Res.Row(
                ("resource_id", 2),
                ("sku_name", "Standard"),
                ("allocation_method", "Static"),
                ("location", "eastus2"),
                ("is_orphan", false)));

        var results = NewCalculator(prices).Calculate(resources, 99);

        // [r.resource_id for r in results] == [1]
        Assert.Equal(new[] { 1 }, results.Select(r => r.ResourceId).ToArray());
        // results[0].payg_monthly == monthly_price
        Assert.NotNull(results[0].PaygMonthly);
        Assert.Equal(monthlyPrice, results[0].PaygMonthly!.Value, 5);
        // get_price.assert_called_once()
        Assert.Equal(1, callCount);
    }

    /// <summary>
    /// Paridad de la IP huérfana costeada: estado "calculated" (default), RI no aplica,
    /// razón "IPs no soportan RI", sin RI ni storage, nota con category=orphan.
    /// </summary>
    [Fact]
    public void Calculate_OrphanWithPrice_SetsPaygAndNoRi()
    {
        var monthlyPrice = MonthlyFromHourly(0.005); // = 3.65

        var prices = new FakePriceRepository
        {
            GetPublicIpMonthlyPriceFn = (_, _, _) => monthlyPrice,
        };

        var resources = Res.Rows(
            Res.Row(
                ("resource_id", 7),
                ("sku_name", "Standard"),
                ("allocation_method", "Static"),
                ("location", "eastus2"),
                ("is_orphan", true)));

        var result = Assert.Single(NewCalculator(prices).Calculate(resources, 1));

        Assert.Equal("calculated", result.CalculationStatus);
        Assert.NotNull(result.PaygMonthly);
        Assert.Equal(monthlyPrice, result.PaygMonthly!.Value, 5);
        Assert.False(result.RiApplies);
        Assert.Equal("IPs no soportan RI", result.RiNotApplicableReason);
        Assert.Null(result.Ri1yMonthly);
        Assert.Null(result.Ri3yMonthly);
        Assert.NotNull(result.CalculationNotes);
        Assert.Contains("category=orphan", result.CalculationNotes!);
    }

    /// <summary>
    /// Cuando el repo no encuentra precio (None/null), la IP huérfana queda en "manual_required"
    /// con payg_monthly null.
    /// </summary>
    [Fact]
    public void Calculate_OrphanWithoutPrice_IsManualRequired()
    {
        var prices = new FakePriceRepository
        {
            GetPublicIpMonthlyPriceFn = (_, _, _) => null,
        };

        var resources = Res.Rows(
            Res.Row(
                ("resource_id", 3),
                ("sku_name", "Standard"),
                ("allocation_method", "Static"),
                ("location", "eastus2"),
                ("is_orphan", true)));

        var result = Assert.Single(NewCalculator(prices).Calculate(resources, 1));

        Assert.Equal("manual_required", result.CalculationStatus);
        Assert.Null(result.PaygMonthly);
    }

    /// <summary>
    /// Sin IPs huérfanas no se devuelve ningún resultado y nunca se consulta precio.
    /// </summary>
    [Fact]
    public void Calculate_NoOrphans_ReturnsEmptyAndDoesNotQueryPrice()
    {
        var callCount = 0;
        var prices = new FakePriceRepository
        {
            GetPublicIpMonthlyPriceFn = (_, _, _) =>
            {
                callCount++;
                return MonthlyFromHourly(0.005);
            },
        };

        var resources = Res.Rows(
            Res.Row(
                ("resource_id", 5),
                ("sku_name", "Standard"),
                ("allocation_method", "Static"),
                ("location", "eastus2"),
                ("is_orphan", false)));

        var results = NewCalculator(prices).Calculate(resources, 1);

        Assert.Empty(results);
        Assert.Equal(0, callCount);
    }
}
