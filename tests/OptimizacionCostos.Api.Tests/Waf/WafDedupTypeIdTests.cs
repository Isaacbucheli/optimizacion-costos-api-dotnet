using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Guarda de recommendationTypeId en dedup/consolidación (spec 2026-07-21): dos entradas con tipos
/// ARM conocidos y DISJUNTOS son recomendaciones distintas de Defender — jamás deben fusionarse,
/// por parecidos que sean los títulos. Tipos desconocidos (CSV/manuales) conservan el comportamiento
/// actual.
/// </summary>
public sealed class WafDedupTypeIdTests
{
    private static WafCandidate Candidate(
        int id, string titleEs, byte pillar = 3, IReadOnlyList<string>? typeIds = null, int? recId = null) => new(
        CanonicalId: id, RecommendationId: recId ?? id, MatrixCode: $"3.{id}", PillarNumber: pillar,
        AdvisorName: titleEs, AdvisorCategory: "Security", ReviewScopeEs: titleEs,
        BenefitEs: null, ClientActionEs: null, BitActionEs: null,
        ResourcesText: "", CompletionPct: 0, RemediationStartDate: null, ExecutionLog: null,
        TypeIds: typeIds);

    // ------------------------- IsTypeCompatible (resolver) -------------------------

    [Fact]
    public void Compatible_cuando_algun_lado_no_tiene_tipo_conocido()
    {
        Assert.True(WafDedupResolverFactory.IsTypeCompatible(null, "aaa"));
        Assert.True(WafDedupResolverFactory.IsTypeCompatible(new List<string>(), "aaa"));
        Assert.True(WafDedupResolverFactory.IsTypeCompatible(new List<string> { "bbb" }, null));
    }

    [Fact]
    public void Compatible_solo_si_el_tipo_del_row_esta_entre_los_de_la_canonica()
    {
        Assert.True(WafDedupResolverFactory.IsTypeCompatible(new List<string> { "aaa", "bbb" }, "AAA"));
        Assert.False(WafDedupResolverFactory.IsTypeCompatible(new List<string> { "bbb" }, "aaa"));
    }

    // ------------------------- Consolidación -------------------------

    [Fact]
    public async Task Consolidacion_titulos_gemelos_con_tipos_disjuntos_no_fusiona()
    {
        // Sin la guarda, ratio 1.0 mismo pilar => merge determinista. Con tipos disjuntos, jamás.
        var service = new WafConsolidationService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WafConsolidationService>.Instance);
        var candidates = new[]
        {
            Candidate(1, "Cuentas deshabilitadas con permisos de propietario deben eliminarse", typeIds: ["aaa"]),
            Candidate(2, "Cuentas deshabilitadas con permisos de propietario deben eliminarse", typeIds: ["bbb"]),
        };

        var merges = await service.PlanConsolidationAsync(candidates, aiConfirm: null);

        Assert.Empty(merges);
    }

    [Fact]
    public async Task Consolidacion_titulos_gemelos_con_mismo_tipo_si_fusiona()
    {
        var service = new WafConsolidationService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WafConsolidationService>.Instance);
        var candidates = new[]
        {
            Candidate(1, "Cuentas deshabilitadas con permisos de propietario deben eliminarse", typeIds: ["aaa"]),
            Candidate(2, "Cuentas deshabilitadas con permisos de propietario deben eliminarse", typeIds: ["aaa"]),
        };

        var merges = await service.PlanConsolidationAsync(candidates, aiConfirm: null);

        Assert.Single(merges);
    }

    [Fact]
    public async Task Consolidacion_sin_tipos_conocidos_conserva_comportamiento_actual()
    {
        var service = new WafConsolidationService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WafConsolidationService>.Instance);
        var candidates = new[]
        {
            Candidate(1, "Cuentas deshabilitadas con permisos de propietario deben eliminarse"),
            Candidate(2, "Cuentas deshabilitadas con permisos de propietario deben eliminarse"),
        };

        var merges = await service.PlanConsolidationAsync(candidates, aiConfirm: null);

        Assert.Single(merges); // gemelas sin typeId: merge determinista como hoy
    }
}
