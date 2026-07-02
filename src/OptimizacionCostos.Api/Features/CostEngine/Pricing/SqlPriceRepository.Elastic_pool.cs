namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Port de <c>get_elastic_pool_prices</c> / <c>_select_elastic_pool_price</c> de
/// price_repository.py (servicio "elastic_pool"). Paridad 1:1.
///
/// Peculiaridades:
///   - Modelo DTU (Basic/Standard/Premium): meter sku "{N} DTU..." con unidad por día → /24.
///   - Modelo vCore (GeneralPurpose/BusinessCritical/Hyperscale): meter sku "{N} vCore" por hora.
///   - Match EXACTO o null: nunca aproxima.
///   - El fallback de IA NO se porta (queda fuera de alcance): si el selector determinista
///     no encuentra item, devuelve null (como cuando assist está deshabilitado en Python).
/// </summary>
public sealed partial class SqlPriceRepository
{
    /// <summary>
    /// Port de <c>get_elastic_pool_prices</c>. Precio PAYG de un Elastic Pool; null si no hay
    /// match exacto verificado. Service "SQL Database", fetch_query "elastic_pool {region}".
    /// </summary>
    public ElasticPoolPrices? GetElasticPoolPrices(string skuTier, string skuFamily, int capacity, string region)
    {
        const string serviceName = "SQL Database";
        var fetchQuery = $"elastic_pool {region}";

        // refresh-si-rancio por fetch_query (lookup amplio por región).
        RefreshByFetchQueryIfStale(fetchQuery, () => _client.FetchElasticPoolPrices(region));

        var cached = _cache.QueryCached(serviceName, region);
        return SelectElasticPoolPrice(cached, skuTier, skuFamily, capacity);
    }

    /// <summary>
    /// Port de <c>_select_elastic_pool_price</c>. Devuelve el precio del pool normalizado a
    /// $/hora o null si no hay match EXACTO. Blindaje: exige capacidad exacta y unidad esperada;
    /// nunca aproxima.
    /// </summary>
    internal static ElasticPoolPrices? SelectElasticPoolPrice(
        IReadOnlyList<PriceRow> cached, string skuTier, string skuFamily, int capacity)
    {
        var tierLower = (skuTier ?? string.Empty).ToLowerInvariant();
        // Python: family_lower = (sku_family or "").lower() or "gen5"
        var familyLower = (skuFamily ?? string.Empty).ToLowerInvariant();
        if (familyLower.Length == 0)
            familyLower = "gen5";
        var cap = capacity;
        if (cap <= 0)
            return null;

        // pool = [c for c in cached if "Elastic Pool" in product_name]  (substring case-sensitive)
        var pool = cached
            .Where(c => (c.ProductName ?? string.Empty).Contains("Elastic Pool"))
            .ToList();

        // Modelo DTU (Basic/Standard/Premium): meter '{N} DTU Pack', unidad por día.
        if (DtuPoolTiers.TryGetValue(tierLower, out var tierName))
        {
            var dtuCandidates = pool
                .Where(c => PriceSelectors.IsConsumption(c)
                    && (c.ProductName ?? string.Empty).Contains($"Elastic Pool - {tierName}")
                    && c.SkuNameLower.StartsWith($"{cap} dtu", StringComparison.Ordinal)
                    && c.UnitOfMeasureLower.Contains("day"))
                .ToList();
            var dtuItem = dtuCandidates.Count > 0 ? dtuCandidates[0] : null;
            if (dtuItem is null)
                return null;
            var pricePerDay = dtuItem.RetailPrice;
            if (pricePerDay is null)
                return null;
            return new ElasticPoolPrices(
                PaygHourly: (double)pricePerDay / 24.0,
                Model: "DTU",
                MeterName: dtuItem.MeterName,
                MatchedSku: dtuItem.SkuName,
                PaygMeterId: dtuItem.MeterId);
        }

        // Modelo vCore (GeneralPurpose/BusinessCritical/Hyperscale): meter '{N} vCore', por hora.
        if (VcorePoolTierTokens.TryGetValue(tierLower, out var tierToken))
        {
            var vcoreCandidates = pool
                .Where(c => PriceSelectors.IsConsumption(c)
                    && (c.ProductName ?? string.Empty).Contains(tierToken)
                    && (c.ProductName ?? string.Empty).Contains("Compute")
                    && c.ProductNameLower.Replace("-", string.Empty).Contains(familyLower)
                    && c.SkuNameLower == $"{cap} vcore"
                    && c.UnitOfMeasureLower.Contains("hour"))
                .ToList();
            var vcoreItem = vcoreCandidates.Count > 0 ? PriceSelectors.PreferPrimaryHourly(vcoreCandidates) : null;
            if (vcoreItem is null)
                return null;
            var priceHourly = vcoreItem.RetailPrice;
            if (priceHourly is null)
                return null;
            return new ElasticPoolPrices(
                PaygHourly: (double)priceHourly,
                Model: "vCore",
                MeterName: vcoreItem.MeterName,
                MatchedSku: vcoreItem.SkuName,
                PaygMeterId: vcoreItem.MeterId);
        }

        return null;
    }
}
