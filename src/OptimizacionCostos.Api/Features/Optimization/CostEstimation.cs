using OptimizacionCostos.Api.Features.CostEngine.Calculators;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.Optimization;

/// <summary>
/// Ahorro estimado por hallazgo. Reusa el motor de precios; nunca inventa cifra (devuelve null si
/// no hay precio). Port de optimization/cost_estimation.py. Reusa la derivación de tier/region del
/// ManagedDiskCalculator del motor de costos.
/// </summary>
public interface ICostEstimation
{
    double? DiskMonthlySavings(string diskSku, int? sizeGb, string region);
    double? PublicIpMonthlySavings(string skuName, string region);
    double? AppServicePlanMonthlySavings(string skuName, string region, bool isLinux);
    double? VmComputeMonthlySavings(string vmSize, string region, string osType);
    double? AhbMonthlySavings(string vmSize, string region);
    double? SnapshotMonthlySavings(string? snapshotSku, int? sizeGb, string region);
}

public sealed class CostEstimation(IPriceRepository prices, IPricingConstants constants, ILogger<CostEstimation> logger) : ICostEstimation
{
    private T? Safe<T>(Func<T?> fn) where T : struct
    {
        try { return fn(); }
        catch (Exception ex) { logger.LogWarning(ex, "cost_estimation fallo type={Type}", ex.GetType().Name); return null; }
    }

    public double? DiskMonthlySavings(string diskSku, int? sizeGb, string region)
    {
        var tier = ManagedDiskCalculator.DeriveDiskTier(diskSku ?? "", sizeGb);
        var normRegion = ManagedDiskCalculator.NormalizeRegion(region ?? "");
        if (string.IsNullOrEmpty(tier) || string.IsNullOrEmpty(normRegion)) return null;
        return Safe(() => prices.GetDiskPrice(tier, normRegion));
    }

    public double? PublicIpMonthlySavings(string skuName, string region) =>
        Safe(() => prices.GetPublicIpMonthlyPrice(skuName, region, "Static"));

    public double? AppServicePlanMonthlySavings(string skuName, string region, bool isLinux)
    {
        try
        {
            var hourly = prices.GetAppServicePrices(skuName, region, isLinux).PaygHourly;
            return hourly is null ? null : hourly.Value * constants.HoursPerMonth();
        }
        catch (Exception ex) { logger.LogWarning(ex, "cost_estimation appservice fallo type={Type}", ex.GetType().Name); return null; }
    }

    public double? VmComputeMonthlySavings(string vmSize, string region, string osType)
    {
        try
        {
            var hourly = prices.GetVmPrices(vmSize, region, osType).PaygHourly;
            return hourly is null ? null : hourly.Value * constants.HoursPerMonth();
        }
        catch (Exception ex) { logger.LogWarning(ex, "cost_estimation vm fallo type={Type}", ex.GetType().Name); return null; }
    }

    public double? AhbMonthlySavings(string vmSize, string region)
    {
        try
        {
            var win = prices.GetVmPrices(vmSize, region, "Windows").PaygHourly;
            var lnx = prices.GetVmPrices(vmSize, region, "Linux").PaygHourly;
            if (win is null || lnx is null || win.Value <= lnx.Value) return null;
            return (win.Value - lnx.Value) * constants.HoursPerMonth();
        }
        catch (Exception ex) { logger.LogWarning(ex, "cost_estimation ahb fallo type={Type}", ex.GetType().Name); return null; }
    }

    public double? SnapshotMonthlySavings(string? snapshotSku, int? sizeGb, string region)
    {
        if (sizeGb is null or <= 0) return null;
        var perGb = Safe(() => prices.GetSnapshotPricePerGb(region, snapshotSku));
        return perGb is null ? null : perGb.Value * sizeGb.Value;
    }
}
