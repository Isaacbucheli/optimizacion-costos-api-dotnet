using System.Text.Json.Nodes;
using OptimizacionCostos.Api.Features.Inventory;
using OptimizacionCostos.Api.Features.Optimization;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Optimization;

/// <summary>
/// Lógica pura de los checks de B6 (sin Azure ni BD): filtrado de hallazgos y uso de la
/// estimación de ahorro. Replican app/optimization/checks/*.py.
/// </summary>
public sealed class OptimizationChecksTests
{
    private sealed class FakeCost : ICostEstimation
    {
        public double? DiskMonthlySavings(string s, int? g, string r) => 12.5;
        public double? PublicIpMonthlySavings(string s, string r) => 3.65;
        public double? AppServicePlanMonthlySavings(string s, string r, bool l) => 50.0;
        public double? VmComputeMonthlySavings(string s, string r, string o) => 100.0;
    }

    private static List<RgRow> Rows(string jsonArray) =>
        (JsonNode.Parse(jsonArray) as JsonArray)!.Select(n => new RgRow(n)).ToList();

    [Fact]
    public void HayChecksRegistrados_Siete()
    {
        Assert.Equal(7, OptimizationChecks.Registered.Count);
        Assert.Contains(OptimizationChecks.Registered, c => c.CheckId == "orphaned_disks");
    }

    [Fact]
    public void OrphanedDisks_MarcaUnattachedConAhorro()
    {
        var rows = Rows("""
            [{"id":"/d/1","name":"disk-1","type":"microsoft.compute/disks","location":"eastus","sku":"Premium_LRS","diskSizeGB":128,"diskState":"Unattached"},
             {"id":"/d/2","name":"disk-2","type":"microsoft.compute/disks","location":"eastus","sku":"Premium_LRS","diskSizeGB":64,"diskState":"Attached","managedBy":"/vm/x"}]
            """);
        var f = OptimizationChecks.OrphanedDisks.BuildFindings(OptimizationChecks.OrphanedDisks, rows, "sub-1", new FakeCost());
        var only = Assert.Single(f); // el "Attached" con managedBy se descarta
        Assert.Equal("disk-1", only.ResourceName);
        Assert.Equal(12.5, only.EstimatedMonthlySavings);
        Assert.Equal("cost_waste", only.Category);
    }

    [Fact]
    public void EmptyAppServicePlans_SkuFreeSinAhorro()
    {
        var rows = Rows("""
            [{"id":"/p/1","name":"plan-free","type":"microsoft.web/serverfarms","location":"eastus","numberOfSites":0,"sku":"F1","isLinux":true},
             {"id":"/p/2","name":"plan-pago","type":"microsoft.web/serverfarms","location":"eastus","numberOfSites":0,"sku":"P1v3","isLinux":false}]
            """);
        var f = OptimizationChecks.EmptyAppServicePlans.BuildFindings(OptimizationChecks.EmptyAppServicePlans, rows, "sub-1", new FakeCost());
        Assert.Equal(2, f.Count);
        Assert.Null(f.Single(x => x.ResourceName == "plan-free").EstimatedMonthlySavings); // free → sin ahorro
        Assert.Equal(50.0, f.Single(x => x.ResourceName == "plan-pago").EstimatedMonthlySavings);
    }

    [Fact]
    public void LbAppGw_ConBackendSeDescarta()
    {
        var rows = Rows("""
            [{"id":"/lb/1","name":"lb-vacio","type":"microsoft.network/loadbalancers","location":"eastus","backendCount":0,"sku":"Standard"},
             {"id":"/lb/2","name":"lb-ok","type":"microsoft.network/loadbalancers","location":"eastus","backendCount":2,"sku":"Standard"}]
            """);
        var f = OptimizationChecks.LbAppGwNoBackend.BuildFindings(OptimizationChecks.LbAppGwNoBackend, rows, "sub-1", new FakeCost());
        Assert.Single(f);
        Assert.Equal("lb-vacio", f[0].ResourceName);
    }

    [Fact]
    public void Fingerprint_EstableYDistintoPorRecurso()
    {
        var a = new Finding("orphaned_disks", "cost_waste", "medium", "s", "/D/1", "d1", "t", "eastus", new Dictionary<string, object?>(), null);
        var b = a with { AzureResourceId = "/d/1" }; // mismo recurso, distinto case → mismo fingerprint
        Assert.Equal(a.Fingerprint(7), b.Fingerprint(7));
        var c = a with { AzureResourceId = "/d/2" };
        Assert.NotEqual(a.Fingerprint(7), c.Fingerprint(7));
    }
}
