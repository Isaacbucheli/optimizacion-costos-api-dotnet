using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Features.CostEngine;
using OptimizacionCostos.Api.Features.FinOpsData;
using Xunit;

namespace OptimizacionCostos.Api.Tests.FinOpsData;

public sealed class FakeFinOpsRefData : IFinOpsRefData
{
    public Dictionary<string, bool> Eligibility { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Throws { get; set; }
    public IReadOnlyCollection<string>? LastQuery { get; private set; }

    /// <summary>Seed opcional para GetRegionNamesAsync/GetResourceTypesAsync (default: vacíos).</summary>
    public Dictionary<string, string> Regions { get; } = new();
    public Dictionary<string, FinOpsResourceTypeInfo> ResourceTypes { get; } = new();

    public Task<IReadOnlyDictionary<string, bool>> GetRiEligibilityAsync(IReadOnlyCollection<string> meterIds, CancellationToken ct = default)
    {
        if (Throws) throw new InvalidOperationException("SQL down");
        LastQuery = meterIds;
        IReadOnlyDictionary<string, bool> result =
            Eligibility.Where(kv => meterIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<string, string>> GetRegionNamesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(Regions);
    public Task<IReadOnlyDictionary<string, FinOpsResourceTypeInfo>> GetResourceTypesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, FinOpsResourceTypeInfo>>(ResourceTypes);
}

public sealed class RiEligibilityEnricherTests
{
    private static CostResult R(int id, string? meter)
        => new(id, 1, "vms") { PaygMeterId = meter };

    [Fact]
    public void Enrich_AsignaEligibleNotEligibleUnknown()
    {
        var fake = new FakeFinOpsRefData();
        fake.Eligibility["m-si"] = true;
        fake.Eligibility["m-no"] = false;
        var rows = new List<CostResult> { R(1, "m-si"), R(2, "m-no"), R(3, "m-ausente"), R(4, null) };

        new RiEligibilityEnricher(fake, NullLogger<RiEligibilityEnricher>.Instance).Enrich(rows);

        Assert.Equal("eligible", rows[0].RiEligibility);
        Assert.Equal("not_eligible", rows[1].RiEligibility);
        Assert.Equal("unknown", rows[2].RiEligibility);
        Assert.Equal("unknown", rows[3].RiEligibility);
    }

    [Fact]
    public void Enrich_ConsultaSoloMetersDistintosNoNulos()
    {
        var fake = new FakeFinOpsRefData();
        var rows = new List<CostResult> { R(1, "m1"), R(2, "m1"), R(3, null) };
        new RiEligibilityEnricher(fake, NullLogger<RiEligibilityEnricher>.Instance).Enrich(rows);
        Assert.Equal(new[] { "m1" }, fake.LastQuery!.ToArray());
    }

    [Fact]
    public void Enrich_SiFallaElLookup_NoRompeYQuedaNull()
    {
        var fake = new FakeFinOpsRefData { Throws = true };
        var rows = new List<CostResult> { R(1, "m1") };
        var ex = Record.Exception(() =>
            new RiEligibilityEnricher(fake, NullLogger<RiEligibilityEnricher>.Instance).Enrich(rows));
        Assert.Null(ex);
        Assert.Null(rows[0].RiEligibility); // el cálculo nunca se bloquea por FinOps data
    }

    [Fact]
    public void Enrich_ListaVacia_NoConsulta()
    {
        var fake = new FakeFinOpsRefData();
        new RiEligibilityEnricher(fake, NullLogger<RiEligibilityEnricher>.Instance).Enrich([]);
        Assert.Null(fake.LastQuery);
    }
}
