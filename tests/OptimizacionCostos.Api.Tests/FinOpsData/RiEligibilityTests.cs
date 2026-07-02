using OptimizacionCostos.Api.Features.FinOpsData;
using Xunit;

namespace OptimizacionCostos.Api.Tests.FinOpsData;

public sealed class RiEligibilityTests
{
    [Fact]
    public void Label_MeterEnMapa_TrueYFalse()
    {
        var map = new Dictionary<string, bool> { ["m1"] = true, ["m2"] = false };
        Assert.Equal("eligible", RiEligibility.Label("m1", map));
        Assert.Equal("not_eligible", RiEligibility.Label("m2", map));
    }

    [Fact]
    public void Label_MeterAusenteONulo_Unknown()
    {
        var map = new Dictionary<string, bool> { ["m1"] = true };
        // El CSV es whitelist: ausencia NUNCA es not_eligible (Spot/LowPriority no aparecen).
        Assert.Equal("unknown", RiEligibility.Label("otro", map));
        Assert.Equal("unknown", RiEligibility.Label(null, map));
        Assert.Equal("unknown", RiEligibility.Label("", map));
    }

    [Fact]
    public void MapEligibility_SemanticaValidada_SpendEsRiUsageEsSp()
    {
        // Evidencia empírica 2026-07-02 (ver comentario en RiEligibility):
        // Container Apps vCPU (sin RI, con SP) → "Not Eligible","Eligible"
        var containerApps = SqlFinOpsDataStore.MapEligibility("Not Eligible", "Eligible");
        Assert.False(containerApps.Ri);
        Assert.True(containerApps.Sp);
        // Redis Enterprise E100 (con RI, sin SP) → "Eligible","Not Eligible"
        var redis = SqlFinOpsDataStore.MapEligibility("Eligible", "Not Eligible");
        Assert.True(redis.Ri);
        Assert.False(redis.Sp);
        // D2s v3 Linux (ambos) → "Eligible","Eligible"
        var d2s = SqlFinOpsDataStore.MapEligibility("Eligible", "Eligible");
        Assert.True(d2s.Ri);
        Assert.True(d2s.Sp);
    }

    [Fact]
    public void CategoryMap_CubreLosServiciosDelCatalogo()
    {
        var m = FinOpsServiceCategoryMap.ByServiceKey;
        Assert.Equal("Compute", m["vms"]);
        Assert.Equal("Compute", m["appservice"]);
        Assert.Equal("Storage", m["disks"]);
        Assert.Equal("Databases", m["sql"]);
        Assert.Equal("Databases", m["sql_mi"]);
        Assert.Equal("Databases", m["mysql"]);
        Assert.Equal("Databases", m["elastic_pool"]);
        Assert.Equal("Databases", m["cosmos"]);
        Assert.Equal("Databases", m["redis"]);
        Assert.Equal("Analytics", m["synapse_dw"]);
        Assert.Equal("Networking", m["public_ip"]);
    }
}
