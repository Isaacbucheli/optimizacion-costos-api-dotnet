using System.Text.Json;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Port de app/waf/dedup.py. Scoring determinista (WafText) + confirmación IA reusando el
/// IChatCompletionClient existente (no se construye un HttpClient nuevo para OpenAI).
/// </summary>
public sealed class WafDedupService(IChatCompletionClient chat, AppConfig config) : IWafDedupService
{
    public (decimal Score, string Reason) ScoreCandidate(string advisorName, byte? pillar, WafCandidate candidate)
    {
        // _tokens(advisor_name) y _tokens(_candidate_title(candidate))
        var rowTokens = WafText.Tokens(advisorName);
        var candidateTokens = WafText.Tokens(CandidateTitle(candidate));
        if (rowTokens.Count == 0 || candidateTokens.Count == 0)
            return (0.0m, "sin tokens suficientes");

        // jaccard sobre tokens (overlap/union)
        var overlap = rowTokens.Count(candidateTokens.Contains);
        var union = rowTokens.Count + candidateTokens.Count - overlap;
        var jaccard = union > 0 ? (double)overlap / union : 0.0;

        // sequence: normalize(advisor_name) vs normalize(review_scope_es or advisor_name)
        var target = !string.IsNullOrEmpty(candidate.ReviewScopeEs)
            ? candidate.ReviewScopeEs
            : (candidate.AdvisorName ?? "");
        var sequence = WafText.SequenceRatio(
            WafText.NormalizeText(advisorName), WafText.NormalizeText(target));

        // same_pillar = pillar_number (truthy) AND candidate.pillar == pillar
        var samePillar = pillar is > 0 && candidate.PillarNumber == pillar.Value;
        var pillarBonus = samePillar ? 0.12 : -0.15;

        var score = Math.Max(0.0, Math.Min(1.0, (jaccard * 0.55) + (sequence * 0.33) + pillarBonus));
        var rounded = Math.Round(score, 4, MidpointRounding.ToEven); // round(score, 4) de Python (banker's)
        var reason = $"tokens {overlap}/{union}, similitud {sequence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";
        return ((decimal)rounded, reason);
    }

    public IReadOnlyList<(WafCandidate Candidate, decimal Score, string Reason)> RankCandidates(
        string advisorName, byte? pillar, IReadOnlyList<WafCandidate> candidates, decimal minScore = 0.08m)
    {
        var scored = new List<(WafCandidate Candidate, decimal Score, string Reason)>();
        foreach (var candidate in candidates)
        {
            var (score, reason) = ScoreCandidate(advisorName, pillar, candidate);
            if (score >= minScore)
                scored.Add((candidate, score, reason));
        }
        // sort por score descendente (estable: List.Sort no lo es, usar OrderByDescending estable)
        return scored.OrderByDescending(item => item.Score).ToList();
    }

    public async Task<WafDupConfirmation?> AiConfirmDuplicateAsync(
        string advisorName, string advisorCategory, IReadOnlyList<WafCandidate> candidates,
        CancellationToken ct = default)
    {
        // config["configured"] and candidates
        if (!IsConfigured() || candidates.Count == 0)
            return null;

        // allowed_ids = {int(item.canonical_id) for item in candidates}
        var allowedIds = candidates.Select(c => c.CanonicalId).ToHashSet();

        // options = candidates[:MAX_CANDIDATES_TO_AI]
        var options = candidates.Take(WafConstants.MaxCandidatesToAi).Select(item => new
        {
            canonical_id = item.CanonicalId,
            pillar_number = (int)item.PillarNumber,
            titulo_actual = !string.IsNullOrEmpty(item.ReviewScopeEs) ? item.ReviewScopeEs : (item.AdvisorName ?? ""),
            advisor_name = item.AdvisorName ?? "",
        }).ToArray();

        var optionsJson = JsonSerializer.Serialize(options, JsonOpts);
        var prompt = string.Format(WafPrompts.ConfirmDuplicateTemplate, advisorName, advisorCategory, optionsJson);

        // Retry 3 intentos con backoff 2^attempt (paridad con dedup.py). Cualquier fallo
        // (HTTP/red/JSON) reintenta; en el 3er fallo devuelve null sin abortar el sync.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Reusa IChatCompletionClient (síncrono). El prompt es autocontenido → va como user.
                var content = await Task.Run(() => chat.Complete("", prompt), ct);
                if (string.IsNullOrWhiteSpace(content))
                    throw new InvalidOperationException("respuesta IA vacia");

                using var doc = WafAiJson.ParseDedup(content);
                var root = doc.RootElement;

                if (!root.GetBoolTruthy("match"))
                    return null;

                var canonicalId = root.GetIntOrZero("canonical_id");
                if (!allowedIds.Contains(canonicalId))
                    return null;

                var confidence = Math.Max(0.0, Math.Min(1.0, root.GetDoubleOrNull("confidence") ?? 0.0));
                var reasonRaw = root.GetString("reason");
                var reason = string.IsNullOrEmpty(reasonRaw) ? "Duplicado confirmado por Azure OpenAI" : reasonRaw;
                if (reason.Length > 500) reason = reason[..500];

                return new WafDupConfirmation(canonicalId, (decimal)confidence, reason);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                if (attempt == 2) return null;
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct); }
                catch (OperationCanceledException) { throw; }
            }
        }
        return null;
    }

    // _candidate_title: review_scope_es + " " + advisor_name (solo título/ámbito, NO el bloque completo)
    private static string CandidateTitle(WafCandidate candidate) =>
        string.Join(" ", (candidate.ReviewScopeEs ?? ""), (candidate.AdvisorName ?? ""));

    // azure_openai_config_status()["configured"]: endpoint + key + deployment + api_version
    private bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(config.AzureOpenAiEndpoint)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiKey)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiDeployment)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiVersion);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // ensure_ascii=False
    };
}
