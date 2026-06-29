using System.Text;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Port de app/waf/ai_curator.py. Reusa el IChatCompletionClient existente (no construye un
/// HttpClient nuevo para OpenAI). NO loguea prompts ni claves.
/// </summary>
public sealed class WafCuratorService(IChatCompletionClient chat, AppConfig config) : IWafCuratorService
{
    public (bool Configured, string Deployment, string ApiVersion, bool HasKey) GetConfigStatus()
    {
        var endpoint = (config.AzureOpenAiEndpoint ?? "").Trim();
        var key = (config.AzureOpenAiApiKey ?? "").Trim();
        var deployment = (config.AzureOpenAiDeployment ?? "").Trim();
        var apiVersion = string.IsNullOrWhiteSpace(config.AzureOpenAiApiVersion)
            ? "2025-04-01-preview" : config.AzureOpenAiApiVersion.Trim();
        var configured = endpoint.Length > 0 && key.Length > 0 && deployment.Length > 0 && apiVersion.Length > 0;
        return (configured, deployment, apiVersion, key.Length > 0);
    }

    public async Task<WafCurationResult> AnalyzeAsync(WafCanonical recommendation, CancellationToken ct = default)
    {
        var status = GetConfigStatus();
        if (!status.Configured)
            throw new InvalidOperationException(
                "Azure OpenAI no esta configurado. Valida AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, "
                + "AZURE_OPENAI_DEPLOYMENT y AZURE_OPENAI_API_VERSION.");

        var prompt = BuildPrompt(recommendation);

        // Port de analyze_recommendation_with_ai: 4 intentos. El IChatCompletionClient esconde la
        // capa HTTP (no expone 429/503/Retry-After), así que se reintenta ante CUALQUIER fallo con
        // backoff 2^(attempt+1) cap 30s (paridad de la escalera de espera del Python).
        string? raw = null;
        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                raw = await Task.Run(() => chat.Complete("", prompt), ct);
                if (string.IsNullOrWhiteSpace(raw))
                    throw new InvalidOperationException("respuesta IA vacia");
                last = null;
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt >= 3) break;
                var wait = Math.Min(Math.Pow(2, attempt + 1), 30.0);
                try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); }
                catch (OperationCanceledException) { throw; }
            }
        }
        if (raw is null || last is not null)
            throw new InvalidOperationException(
                "No se pudo obtener respuesta de Azure OpenAI para la curacion.", last);

        using var doc = WafAiJson.ParseCurator(raw);
        var result = Normalize(doc.RootElement);
        var rawModel = raw.Length > 4000 ? raw[..4000] : raw;
        return result with { RawModelText = rawModel };
    }

    // ai_curator.py::_prompt — texto EXACTO via WafPrompts.CuratorTemplate.
    private static string BuildPrompt(WafCanonical r)
    {
        var pillars = PillarsJson();
        // Python f-string interpola los valores actuales tal cual (los del row de DB).
        return string.Format(
            WafPrompts.CuratorTemplate,
            pillars,                 // {0}
            r.CanonicalId,           // {1}
            r.AdvisorName,           // {2}
            r.AdvisorCategory,       // {3}
            r.PillarNumber,          // {4} pillar_number actual
            r.ReviewScopeEs,         // {5}
            r.BenefitEs,             // {6}
            r.ClientActionEs,        // {7}
            r.BitActionEs);          // {8}
    }

    // json.dumps(PILLAR_NAMES, ensure_ascii=False) — orden 1..5, separadores ", " y ": ".
    private static string PillarsJson()
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var kv in WafPrompts.CuratorPillarNames)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append('"').Append(kv.Key).Append("\": \"").Append(kv.Value).Append('"');
        }
        sb.Append('}');
        return sb.ToString();
    }

    // ai_curator.py::_normalize
    private static WafCurationResult Normalize(System.Text.Json.JsonElement data)
    {
        var decision = (data.GetString("decision") ?? "review").Trim().ToLowerInvariant();
        if (!WafPrompts.ValidDecisions.Contains(decision))
            decision = "review";

        // int(data.get("pillar_number") or 2); si no en PILLAR_NAMES -> 2
        var pillarRaw = data.GetIntOrZero("pillar_number");
        var pillar = pillarRaw == 0 ? 2 : pillarRaw; // "or 2": 0/ausente -> 2
        if (!WafPrompts.CuratorPillarNames.ContainsKey(pillar))
            pillar = 2;

        // max(0, min(1, float(data.get("confidence") or 0)))
        var confidence = Math.Max(0.0, Math.Min(1.0, data.GetDoubleOrNull("confidence") ?? 0.0));

        var exclusionReason = WafAiClean.Clean(data.GetString("exclusion_reason"), 1000);
        var costReason = WafAiClean.Clean(data.GetString("cost_reason"), 1000);
        if (decision == "exclude" && string.IsNullOrEmpty(exclusionReason))
            exclusionReason = !string.IsNullOrEmpty(costReason)
                ? costReason
                : "Exclusion sugerida por AI Curator. Requiere validacion funcional.";

        return new WafCurationResult(
            Decision: decision,
            PossibleAdditionalCost: data.GetBoolTruthy("possible_additional_cost"),
            CostReason: costReason,
            DuplicateGroupKey: WafAiClean.Clean(data.GetString("duplicate_group_key"), 200),
            PillarNumber: (byte)pillar,
            ReviewScopeEs: WafAiClean.Clean(data.GetString("review_scope_es"), 1000),
            BenefitEs: WafAiClean.Clean(data.GetString("benefit_es"), 2000),
            ClientActionEs: WafAiClean.Clean(data.GetString("client_action_es"), 2000),
            BitActionEs: WafAiClean.Clean(data.GetString("bit_action_es"), 2000),
            ExclusionReason: exclusionReason,
            Confidence: (decimal)confidence,
            RawModelText: ""); // se rellena en AnalyzeAsync con raw[:4000]
    }
}
