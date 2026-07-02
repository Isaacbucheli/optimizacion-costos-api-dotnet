using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using OptimizacionCostos.Api.Features.Optimization;
using OptimizacionCostos.Api.Tests.CostEngine;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Optimization;

public sealed class CostEstimationTests
{
    private static CostEstimation Build(FakePriceRepository prices) =>
        new CostEstimation(prices, new FakePricingConstants(), NullLogger<CostEstimation>.Instance);

    [Fact]
    public void Ahb_PremiumEsWindowsMenosLinuxPorHoras()
    {
        var prices = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, os) => os == "Windows"
                ? new VmPrices(0.20, null, null)
                : new VmPrices(0.10, null, null),
        };
        // (0.20 - 0.10) * 730 = 73.0
        Assert.Equal(73.0, Build(prices).AhbMonthlySavings("Standard_D2s_v3", "eastus"));
    }

    [Fact]
    public void Ahb_SinPrecioOWindowsNoMayor_Null()
    {
        var noWin = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, os) => os == "Windows"
                ? new VmPrices(null, null, null)
                : new VmPrices(0.10, null, null),
        };
        Assert.Null(Build(noWin).AhbMonthlySavings("x", "eastus"));

        var winLeLnx = new FakePriceRepository
        {
            GetVmPricesFn = (_, _, _) => new VmPrices(0.10, null, null),
        };
        Assert.Null(Build(winLeLnx).AhbMonthlySavings("x", "eastus")); // win <= lnx → no hay premium
    }

    [Fact]
    public void Snapshot_SizePorPrecioGb()
    {
        var prices = new FakePriceRepository { GetSnapshotPricePerGbFn = (_, _) => 0.05 };
        Assert.Equal(6.4, Build(prices).SnapshotMonthlySavings("Standard_LRS", 128, "eastus")); // 128 * 0.05
    }

    [Fact]
    public void Snapshot_SizeNuloOSinPrecio_Null()
    {
        Assert.Null(Build(new FakePriceRepository { GetSnapshotPricePerGbFn = (_, _) => 0.05 })
            .SnapshotMonthlySavings("Standard_LRS", null, "eastus"));
        Assert.Null(Build(new FakePriceRepository { GetSnapshotPricePerGbFn = (_, _) => null })
            .SnapshotMonthlySavings("Standard_LRS", 128, "eastus"));
    }
}
