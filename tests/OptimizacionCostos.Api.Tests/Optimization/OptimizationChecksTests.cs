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
        public double? AhbMonthlySavings(string s, string r) => 40.0;
        public double? SnapshotMonthlySavings(string? sku, int? g, string r) => g is null ? null : 5.0;
    }

    private static List<RgRow> Rows(string jsonArray) =>
        (JsonNode.Parse(jsonArray) as JsonArray)!.Select(n => new RgRow(n)).ToList();

    [Fact]
    public void OrphanedNics_MarcaLasSinVmNiPrivateEndpoint()
    {
        var rows = Rows("""
            [{"id":"/n/1","name":"nic-huerfana","type":"microsoft.network/networkinterfaces","location":"eastus"},
             {"id":"/n/2","name":"nic-en-uso","type":"microsoft.network/networkinterfaces","location":"eastus","vmId":"/vm/x"}]
            """);
        var f = OptimizationChecks.OrphanedNics.BuildFindings(OptimizationChecks.OrphanedNics, rows, "sub-1", new FakeCost());
        var only = Assert.Single(f);
        Assert.Equal("nic-huerfana", only.ResourceName);
        Assert.Equal("governance", only.Category);
        Assert.Null(only.EstimatedMonthlySavings);
    }

    [Fact]
    public void EmptySubnets_MarcaSinIpConfigsNiDelegaciones()
    {
        var rows = Rows("""
            [{"id":"/vnet1/subnets/vacia","name":"vacia","type":"microsoft.network/virtualnetworks/subnets","location":"eastus","vnet":"vnet1","ipCount":0,"delegCount":0},
             {"id":"/vnet1/subnets/usada","name":"usada","type":"microsoft.network/virtualnetworks/subnets","location":"eastus","vnet":"vnet1","ipCount":3,"delegCount":0}]
            """);
        var f = OptimizationChecks.EmptySubnets.BuildFindings(OptimizationChecks.EmptySubnets, rows, "sub-1", new FakeCost());
        var only = Assert.Single(f);
        Assert.Equal("vacia", only.ResourceName);
        Assert.Null(only.EstimatedMonthlySavings);
    }

    [Fact]
    public void HayChecksRegistrados()
    {
        Assert.Equal(12, OptimizationChecks.Registered.Count);
        Assert.Contains(OptimizationChecks.Registered, c => c.CheckId == "orphaned_disks");
        Assert.Contains(OptimizationChecks.Registered, c => c.CheckId == "orphaned_nics");
        Assert.Contains(OptimizationChecks.Registered, c => c.CheckId == "empty_subnets");
        Assert.Contains(OptimizationChecks.Registered, c => c.CheckId == "vms_without_ha");
        Assert.Contains(OptimizationChecks.Registered, c => c.CheckId == "basic_load_balancers");
        Assert.Contains(OptimizationChecks.Registered, c => c.CheckId == "old_snapshots");
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
    public void VmsWithoutHa_MarcaSinZonaNiAvSetNiVmss()
    {
        var rows = Rows("""
            [{"id":"/vm/1","name":"vm-sola","type":"microsoft.compute/virtualmachines","location":"eastus","vmSize":"Standard_D2s_v3","hasZone":false,"hasAvSet":false,"inVmss":false},
             {"id":"/vm/2","name":"vm-zonal","type":"microsoft.compute/virtualmachines","location":"eastus","vmSize":"Standard_D2s_v3","hasZone":true,"hasAvSet":false,"inVmss":false}]
            """);
        var f = OptimizationChecks.VmsWithoutHa.BuildFindings(OptimizationChecks.VmsWithoutHa, rows, "sub-1", new FakeCost());
        var only = Assert.Single(f);
        Assert.Equal("vm-sola", only.ResourceName);
        Assert.Equal("governance", only.Category);
        Assert.Null(only.EstimatedMonthlySavings);
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

    [Fact]
    public void BasicLoadBalancers_MarcaSoloBasic()
    {
        var rows = Rows("""
            [{"id":"/lb/1","name":"lb-basic","type":"microsoft.network/loadbalancers","location":"eastus","sku":"Basic"},
             {"id":"/lb/2","name":"lb-standard","type":"microsoft.network/loadbalancers","location":"eastus","sku":"Standard"}]
            """);
        var f = OptimizationChecks.BasicLoadBalancers.BuildFindings(OptimizationChecks.BasicLoadBalancers, rows, "sub-1", new FakeCost());
        var only = Assert.Single(f);
        Assert.Equal("lb-basic", only.ResourceName);
        Assert.Null(only.EstimatedMonthlySavings);
    }

    [Fact]
    public void OldSnapshots_MarcaAntiguosConAhorro_OmiteRecientes()
    {
        var rows = Rows("""
            [{"id":"/s/1","name":"snap-viejo","type":"microsoft.compute/snapshots","location":"eastus","created":"2020-01-01T00:00:00Z","sku":"Standard_LRS","diskSizeGB":128,"incremental":false},
             {"id":"/s/2","name":"snap-futuro","type":"microsoft.compute/snapshots","location":"eastus","created":"2099-01-01T00:00:00Z","sku":"Standard_LRS","diskSizeGB":64,"incremental":true}]
            """);
        var f = OptimizationChecks.OldSnapshots.BuildFindings(OptimizationChecks.OldSnapshots, rows, "sub-1", new FakeCost());
        var only = Assert.Single(f); // el "futuro" (reciente) se omite
        Assert.Equal("snap-viejo", only.ResourceName);
        Assert.Equal("cost_waste", only.Category);
        Assert.Equal(5.0, only.EstimatedMonthlySavings); // FakeCost.SnapshotMonthlySavings = 5.0 con size no nulo
    }
}
