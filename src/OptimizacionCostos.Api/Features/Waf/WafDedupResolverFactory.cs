using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>Port de _build_advisor_dedup_resolver (app/routes/waf.py).</summary>
public sealed class WafDedupResolverFactory(
    IWafCatalogStore catalog, IWafDedupService dedup, AppConfig config) : IWafDedupResolverFactory
{
    public Func<SqlConnection, SqlTransaction, AdvisorRow, Task<int?>> Build(
        int clientId, string? createdBy, int aiLimit, out WafDedupResolverState state)
    {
        var s = new WafDedupResolverState();
        state = s;
        var aiConfigured = config.AzureOpenAiEnabled
            && !string.IsNullOrWhiteSpace(config.AzureOpenAiEndpoint)
            && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiKey)
            && !string.IsNullOrWhiteSpace(config.AzureOpenAiDeployment);

        return async (conn, tx, row) =>
        {
            // 1. Alias aprendido (rápido, sin costo, idempotente).
            var alias = await catalog.FindAliasAsync(conn, tx, row.AdvisorName, row.AdvisorCategory, default);
            if (alias is { } aliasId) { s.Merged++; return aliasId; }

            if (!aiConfigured || s.AiCalls >= aiLimit) return null;

            // 2. Candidatos = catálogo activo del cliente (cacheado por corrida).
            s.Candidates ??= await catalog.LoadClientCandidatesAsync(conn, tx, clientId, default);
            if (s.Candidates.Count == 0) return null;

            var pillar = PillarFromCategory(row.AdvisorCategory);
            var ranked = dedup.RankCandidates(row.AdvisorName, pillar, s.Candidates);
            if (ranked.Count == 0) return null;

            var top = ranked.Take(WafConstants.MaxCandidatesToAi).Select(r => r.Candidate).ToList();
            s.AiCalls++;
            var result = await dedup.AiConfirmDuplicateAsync(row.AdvisorName, row.AdvisorCategory, top, default);
            if (result is null || result.Confidence < (decimal)WafConstants.MinAiConfidence) return null;

            // 3. Persiste el alias aprendido (idempotente) y devuelve la canónica.
            await catalog.SaveAliasAsync(conn, tx, row.AdvisorName, row.AdvisorCategory,
                result.CanonicalId, "azure_openai", result.Confidence, createdBy, default);
            s.Merged++;
            return result.CanonicalId;
        };
    }

    private static byte PillarFromCategory(string category)
    {
        var key = WafText.NormalizeText(category).Replace(" ", "");
        return WafConstants.CategoryToPillar.TryGetValue(key, out var p) ? p : WafConstants.DefaultPillar;
    }
}
