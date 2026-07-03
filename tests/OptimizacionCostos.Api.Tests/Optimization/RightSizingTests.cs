using OptimizacionCostos.Api.Features.Optimization;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Optimization;

/// <summary>Lógica pura del right-sizing (Fase 2.5, Grupo B): umbral de subutilización + hallazgo.</summary>
public sealed class RightSizingTests
{
    private sealed class Cost : ICostEstimation
    {
        public double? DiskMonthlySavings(string s, int? g, string r) => null;
        public double? PublicIpMonthlySavings(string s, string r) => null;
        public double? AppServicePlanMonthlySavings(string s, string r, bool l) => null;
        public double? VmComputeMonthlySavings(string s, string r, string o) => 200.0;
        public double? AhbMonthlySavings(string s, string r) => null;
        public double? SnapshotMonthlySavings(string? s, int? g, string r) => null;
        public double? RightSizeMonthlySavings(string s, string r, string o) => 100.0;
    }

    [Theory]
    [InlineData(2.0, 10.0, true)]   // avg y pico bajos → subutilizada
    [InlineData(4.9, 19.9, true)]   // justo bajo el umbral
    [InlineData(6.0, 10.0, false)]  // avg alto
    [InlineData(2.0, 25.0, false)]  // pico alto
    public void IsUnderutilized_SegunUmbrales(double avg, double max, bool expected) =>
        Assert.Equal(expected, RightSizing.IsUnderutilized(avg, max));

    [Fact]
    public void IsUnderutilized_SinMetricasEsFalse()
    {
        Assert.False(RightSizing.IsUnderutilized(null, 10.0));
        Assert.False(RightSizing.IsUnderutilized(2.0, null));
    }

    [Fact]
    public void TryBuild_Subutilizada_DevuelveFindingConAhorro()
    {
        var f = RightSizing.TryBuild("sub", "/vm/1", "vm-ocioso", "eastus2", "Standard_D4s_v3", "Linux", 2.0, 8.0, new Cost());
        Assert.NotNull(f);
        Assert.Equal("underutilized_vms", f!.CheckId);
        Assert.Equal("cost_waste", f.Category);
        Assert.Equal("medium", f.Severity);
        Assert.Equal(100.0, f.EstimatedMonthlySavings);
        Assert.Equal("vm-ocioso", f.ResourceName);
    }

    [Fact]
    public void TryBuild_NoSubutilizada_DevuelveNull() =>
        Assert.Null(RightSizing.TryBuild("sub", "/vm/1", "vm-activo", "eastus2", "Standard_D4s_v3", "Linux", 40.0, 90.0, new Cost()));
}
