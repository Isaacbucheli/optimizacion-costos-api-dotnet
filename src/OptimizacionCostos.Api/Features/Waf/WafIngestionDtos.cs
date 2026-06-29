namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>Resultado de una corrida de ingesta (port del dict de ingest_advisor_rows).</summary>
public sealed record WafIngestionResult(
    int RunId,
    WafIngestionMetrics Metrics,
    int NewRecommendations,
    IReadOnlyList<int> NewCanonicalIds,
    int NewFindings,
    int ResolvedFindings,
    IReadOnlyList<object> Warnings);

/// <summary>
/// Estado mutable de un resolver de dedup por corrida (port del dict state de
/// _build_advisor_dedup_resolver): candidatos cacheados, tope de llamadas IA, fusiones aplicadas.
/// </summary>
public sealed class WafDedupResolverState
{
    public IReadOnlyList<WafCandidate>? Candidates { get; set; }
    public int AiCalls { get; set; }
    public int Merged { get; set; }
}
