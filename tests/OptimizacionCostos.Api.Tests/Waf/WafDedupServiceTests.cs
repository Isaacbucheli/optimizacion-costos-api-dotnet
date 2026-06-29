using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using OptimizacionCostos.Api.Features.Waf;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Scoring determinista de dedup (port de score_candidate). La fórmula combina primitivos ya
/// probados (Jaccard, SequenceRatio); aquí se verifica la combinación + bonus de pilar + clamp.
/// </summary>
public sealed class WafDedupServiceTests
{
    private sealed class NoChat : IChatCompletionClient
    {
        public string Complete(string system, string userJson, int maxCompletionTokens = 500) => "";
    }

    private static WafDedupService NewService() => new(new NoChat(), new AppConfig());

    private static WafCandidate Cand(byte pillar, string reviewScope) => new(
        CanonicalId: 1, RecommendationId: 1, MatrixCode: "5.1", PillarNumber: pillar,
        AdvisorName: "", AdvisorCategory: "Cost", ReviewScopeEs: reviewScope,
        BenefitEs: null, ClientActionEs: null, BitActionEs: null,
        ResourcesText: null, CompletionPct: null, RemediationStartDate: null, ExecutionLog: null);

    [Fact]
    public void ScoreCandidate_IdenticoMismoPilar_EsUno()
    {
        // jaccard 1.0*0.55 + sequence 1.0*0.33 + bonus 0.12 = 1.0 (clamp)
        var (score, _) = NewService().ScoreCandidate("Enable soft delete for blobs", 5,
            Cand(5, "Enable soft delete for blobs"));
        Assert.Equal(1.0m, score);
    }

    [Fact]
    public void ScoreCandidate_IdenticoPilarDistinto_RestaBonus()
    {
        // 0.55 + 0.33 - 0.15 = 0.73
        var (score, _) = NewService().ScoreCandidate("Enable soft delete for blobs", 3,
            Cand(5, "Enable soft delete for blobs"));
        Assert.Equal(0.73m, score);
    }

    [Fact]
    public void ScoreCandidate_SinTokens_EsCero()
    {
        var (score, reason) = NewService().ScoreCandidate("", 5, Cand(5, ""));
        Assert.Equal(0.0m, score);
        Assert.Equal("sin tokens suficientes", reason);
    }

    [Fact]
    public void RankCandidates_FiltraPorUmbralYOrdena()
    {
        var svc = NewService();
        var cands = new[]
        {
            Cand(5, "Enable soft delete for blobs"),       // alto (match exacto)
            Cand(5, "Algo totalmente distinto sin relacion"), // bajo
        };
        var ranked = svc.RankCandidates("Enable soft delete for blobs", 5, cands, 0.5m);
        Assert.Single(ranked); // solo el de score alto pasa el umbral 0.5
        Assert.Equal(1.0m, ranked[0].Score);
    }
}
