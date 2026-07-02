using Microsoft.Extensions.Logging;
using OptimizacionCostos.Api.Features.CostEngine;

namespace OptimizacionCostos.Api.Features.FinOpsData;

public interface IRiEligibilityEnricher
{
    /// <summary>Asigna RiEligibility a cada fila (join batch por PaygMeterId). Best-effort.</summary>
    void Enrich(IReadOnlyList<CostResult> results);
}

public sealed class RiEligibilityEnricher(IFinOpsRefData refData, ILogger<RiEligibilityEnricher> logger)
    : IRiEligibilityEnricher
{
    public void Enrich(IReadOnlyList<CostResult> results)
    {
        var meters = results
            .Where(r => !string.IsNullOrEmpty(r.PaygMeterId))
            .Select(r => r.PaygMeterId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (results.Count == 0) return;

        IReadOnlyDictionary<string, bool> map;
        try
        {
            map = meters.Count == 0
                ? new Dictionary<string, bool>()
                : refData.GetRiEligibilityAsync(meters).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // El cálculo NUNCA se bloquea por datos FinOps: se deja null (el front no muestra nada).
            logger.LogWarning(ex, "RI eligibility enrichment falló; filas quedan sin marcar");
            return;
        }

        foreach (var r in results)
            r.RiEligibility = RiEligibility.Label(r.PaygMeterId, map);
    }
}
