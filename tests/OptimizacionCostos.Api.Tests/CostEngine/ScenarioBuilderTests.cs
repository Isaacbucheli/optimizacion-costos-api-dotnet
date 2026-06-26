using OptimizacionCostos.Api.Features.CostEngine.Scenarios;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Tests de la lógica pura de escenarios (port de _effective_cost_for_scenario,
/// _build_payg_baseline, _build_breakdown de scenarios.py). Sin BD.
/// </summary>
public sealed class ScenarioBuilderTests
{
    private static ScenarioConfig E1 => ScenarioConfig.All[0]; // RI 1Y + depurar
    private static ScenarioConfig E2 => ScenarioConfig.All[1]; // RI 1Y sin depurar
    private static ScenarioConfig E3 => ScenarioConfig.All[2]; // RI 3Y + depurar

    [Fact]
    public void ManualCost_OverridesPayg_AsBase()
    {
        var r = new ScenarioResultRow
        {
            ServiceKey = "vms", IsManualCost = true, ManualMonthlyCost = 50.0,
            PaygMonthly = 999.0, RiApplies = false,
        };
        Assert.Equal(50.0, ScenarioBuilder.EffectiveCostForScenario(r, E2), 5);
    }

    [Fact]
    public void Disk_AttachedStopped_EliminatedWhenConfigured()
    {
        var r = new ScenarioResultRow
        {
            ServiceKey = "disks", PaygMonthly = 9.99,
            CalculationNotes = "category=attached_stopped, tier=E10 LRS",
        };
        Assert.Equal(0.0, ScenarioBuilder.EffectiveCostForScenario(r, E1), 5); // E1 depura
        Assert.Equal(9.99, ScenarioBuilder.EffectiveCostForScenario(r, E2), 5); // E2 no depura
    }

    [Fact]
    public void Disk_Orphan_EliminatedWhenConfigured()
    {
        var r = new ScenarioResultRow
        {
            ServiceKey = "disks", PaygMonthly = 12.5,
            CalculationNotes = "category=orphan, tier=P10 LRS",
        };
        Assert.Equal(0.0, ScenarioBuilder.EffectiveCostForScenario(r, E1), 5);
        Assert.Equal(12.5, ScenarioBuilder.EffectiveCostForScenario(r, E2), 5);
    }

    [Fact]
    public void PublicIp_Orphan_EliminatedWhenConfigured()
    {
        var r = new ScenarioResultRow { ServiceKey = "public_ip", PaygMonthly = 3.65, IpIsOrphan = true };
        Assert.Equal(0.0, ScenarioBuilder.EffectiveCostForScenario(r, E1), 5);
        Assert.Equal(3.65, ScenarioBuilder.EffectiveCostForScenario(r, E2), 5);
    }

    [Fact]
    public void RiApplies_UsesRiWhenScenarioEnablesIt()
    {
        var r = new ScenarioResultRow
        {
            ServiceKey = "vms", PaygMonthly = 100.0, Ri1yMonthly = 80.0, Ri3yMonthly = 60.0,
            RiApplies = true, RiCoverage = null,
        };
        Assert.Equal(80.0, ScenarioBuilder.EffectiveCostForScenario(r, E1), 5); // usa RI 1Y
        Assert.Equal(60.0, ScenarioBuilder.EffectiveCostForScenario(r, E3), 5); // usa RI 3Y
    }

    [Fact]
    public void RiConfirmed_DoesNotApplyRi_NoNewSaving()
    {
        // Si el recurso YA está reservado (ri_coverage="confirmed"), no se cuenta como ahorro nuevo.
        var r = new ScenarioResultRow
        {
            ServiceKey = "vms", PaygMonthly = 100.0, Ri1yMonthly = 80.0,
            RiApplies = true, RiCoverage = "confirmed",
        };
        Assert.Equal(100.0, ScenarioBuilder.EffectiveCostForScenario(r, E1), 5);
    }

    [Fact]
    public void PaygBaseline_SumsPaygAndManual()
    {
        var rows = new List<ScenarioResultRow>
        {
            new() { ServiceKey = "vms", PaygMonthly = 100.0 },
            new() { ServiceKey = "disks", PaygMonthly = 50.0 },
            new() { ServiceKey = "cosmos", IsManualCost = true, ManualMonthlyCost = 25.0, PaygMonthly = null },
            new() { ServiceKey = "redis", PaygMonthly = null }, // no suma
        };
        Assert.Equal(175.0, ScenarioBuilder.BuildPaygBaseline(rows), 5);
    }

    [Fact]
    public void Breakdown_GroupsByServiceWithLabels()
    {
        var rows = new List<ScenarioResultRow>
        {
            new() { ServiceKey = "vms", PaygMonthly = 100.0, RiApplies = false },
            new() { ServiceKey = "vms", PaygMonthly = 50.0, RiApplies = false },
            new() { ServiceKey = "disks", PaygMonthly = 9.99, CalculationNotes = "category=orphan" },
        };
        var bd = ScenarioBuilder.BuildBreakdown(rows, E2); // E2 no depura
        Assert.Equal(2, bd.Count);
        Assert.Equal("vms", bd[0].ServiceKey);
        Assert.Equal("VMs", bd[0].LineLabel);
        Assert.Equal(150.0, bd[0].MonthlyCost, 5);
        Assert.Equal("Discos", bd[1].LineLabel);
        Assert.Equal(9.99, bd[1].MonthlyCost, 5);
    }
}
