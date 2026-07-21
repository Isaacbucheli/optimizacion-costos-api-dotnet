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
            // 0. Atajo determinista por recommendationTypeId (identidad ARM): mismo tipo => misma
            //    recomendación aunque Microsoft haya renombrado el label. Sin costo de IA.
            if (!string.IsNullOrEmpty(row.RecommendationTypeId))
            {
                var byType = await catalog.FindCanonicalByTypeIdAsync(conn, tx, row.RecommendationTypeId, default);
                if (byType is { } typeMatch) { s.Merged++; return typeMatch; }
            }

            // 1. Alias aprendido (rápido, sin costo, idempotente) + guarda: un alias hacia una
            //    canónica de OTRO tipo ARM conocido se ignora (tipos distintos = recs distintas).
            var alias = await catalog.FindAliasAsync(conn, tx, row.AdvisorName, row.AdvisorCategory, default);
            if (alias is { } aliasId)
            {
                var aliasTypeIds = await catalog.GetCanonicalTypeIdsAsync(conn, tx, aliasId, default);
                if (IsTypeCompatible(aliasTypeIds, row.RecommendationTypeId)) { s.Merged++; return aliasId; }
            }

            if (!aiConfigured || s.AiCalls >= aiLimit) return null;

            // 2. Candidatos = catálogo activo del cliente (cacheado por corrida), excluyendo los de
            //    tipo ARM incompatible: la IA nunca puede fusionar tipos distintos conocidos.
            s.Candidates ??= await catalog.LoadClientCandidatesAsync(conn, tx, clientId, default);
            if (s.Candidates.Count == 0) return null;

            var pillar = PillarFromCategory(row.AdvisorCategory);
            var compatible = s.Candidates
                .Where(c => IsTypeCompatible(c.TypeIds, row.RecommendationTypeId))
                .ToList();
            var ranked = dedup.RankCandidates(row.AdvisorName, pillar, compatible);
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

    /// <summary>
    /// Guarda de tipo ARM: compatible si el row no trae typeId (CSV/manual), si la canónica no
    /// tiene tipos conocidos, o si el tipo del row está entre los de la canónica.
    /// </summary>
    internal static bool IsTypeCompatible(IReadOnlyList<string>? canonicalTypeIds, string? rowTypeId)
    {
        if (string.IsNullOrEmpty(rowTypeId)) return true;
        if (canonicalTypeIds is not { Count: > 0 }) return true;
        return canonicalTypeIds.Contains(rowTypeId, StringComparer.OrdinalIgnoreCase);
    }

    private static byte PillarFromCategory(string category)
    {
        var key = WafText.NormalizeText(category).Replace(" ", "");
        return WafConstants.CategoryToPillar.TryGetValue(key, out var p) ? p : WafConstants.DefaultPillar;
    }
}
