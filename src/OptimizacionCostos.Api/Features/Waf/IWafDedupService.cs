namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Deduplicación semántica de recomendaciones de Azure Advisor contra el catálogo ya cargado
/// del cliente (port de app/waf/dedup.py). Prefiltro determinista por similitud de texto +
/// confirmación opcional con Azure OpenAI (solo se fusiona con confianza alta).
/// </summary>
public interface IWafDedupService
{
    /// <summary>
    /// Puntúa qué tan probable es que <paramref name="candidate"/> (ya cargado) sea la MISMA
    /// recomendación que la fila de Advisor <paramref name="advisorName"/>. Port de score_candidate:
    /// Jaccard×0.55 + SequenceRatio×0.33 + bonus pilar (+0.12 mismo / -0.15 distinto), clamp [0,1],
    /// redondeado a 4 decimales. Devuelve (0, "sin tokens suficientes") si falta tokens.
    /// </summary>
    (decimal Score, string Reason) ScoreCandidate(string advisorName, byte? pillar, WafCandidate candidate);

    /// <summary>
    /// Filtra (score &gt;= minScore) y ordena por score descendente. Port de rank_candidates.
    /// minScore por defecto = WafConstants.MinDeterministicScore (0.08).
    /// </summary>
    IReadOnlyList<(WafCandidate Candidate, decimal Score, string Reason)> RankCandidates(
        string advisorName, byte? pillar, IReadOnlyList<WafCandidate> candidates, decimal minScore = 0.08m);

    /// <summary>
    /// Pregunta a Azure OpenAI si la recomendación de Advisor es la MISMA que alguno de los
    /// candidatos (máx WafConstants.MaxCandidatesToAi = 5). Port de ai_confirm_duplicate.
    /// Devuelve {canonical_id, confidence, reason} o null si no hay match claro, IA no
    /// configurada, o canonical_id fuera del conjunto permitido. confidence se clampa a [0,1].
    /// </summary>
    Task<WafDupConfirmation?> AiConfirmDuplicateAsync(
        string advisorName, string advisorCategory, IReadOnlyList<WafCandidate> candidates,
        CancellationToken ct = default);
}
