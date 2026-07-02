namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Servicio "redis": port de <c>get_redis_prices</c> y del selector
/// <c>_select_redis_prices</c> de app/pricing/price_repository.py.
///
/// Peculiaridades portadas 1:1:
///   - Classic (Basic/Standard/Premium) → servicio "Redis Cache" vía
///     <c>fetch_redis_prices(sku_tier or "Standard", region)</c>.
///   - Enterprise/Managed (sku_tier contiene "Enterprise") → servicio
///     "Azure Cache for Redis" vía <c>fetch_azure_managed_redis_prices(region)</c>.
///   - Token de capacidad: el SKU clásico esperado es C{cap} para Basic/Standard y
///     P{cap} para el resto; además se filtra por tokens de capacidad
///     (C/P/E/Enterprise E{cap}) cuando hay coincidencias.
///   - El cache se refresca por fetch_query "redis {sku_tier} {region}".
///   - El fallback de IA (_assistant_select) NO se porta en Fase 1: si la selección
///     determinista no encuentra PAYG, payg_hourly queda en null (como con asistente
///     deshabilitado) y match_strategy en null.
/// </summary>
public sealed partial class SqlPriceRepository
{
    /// <summary>
    /// Python: <c>get_redis_prices(sku_tier, sku_capacity, region)</c>.
    /// </summary>
    public RedisPrices GetRedisPrices(string skuTier, int skuCapacity, string region)
    {
        var fetchQuery = $"redis {skuTier} {region}";

        string serviceName;
        Func<IReadOnlyList<PriceRow>> fetchFn;
        if (!string.IsNullOrEmpty(skuTier) && skuTier.Contains("Enterprise"))
        {
            serviceName = "Azure Cache for Redis";
            fetchFn = () => _client.FetchAzureManagedRedisPrices(region);
        }
        else
        {
            serviceName = "Redis Cache";
            var tierForFetch = string.IsNullOrEmpty(skuTier) ? "Standard" : skuTier;
            fetchFn = () => _client.FetchRedisPrices(tierForFetch, region);
        }

        // Python: if not _is_fetch_query_fresh(fetch_query): fetch + delete_by_fetch_query + insert.
        RefreshByFetchQueryIfStale(fetchQuery, fetchFn);

        var cached = _cache.QueryCached(serviceName, region);
        var selected = SelectRedisPrices(cached, skuTier, skuCapacity);

        // Fallback IA (paridad con Python _select_redis_prices): si el determinista no halló PAYG,
        // la IA elige un meter REAL entre TODOS los Consumption cacheados del servicio Redis.
        if (selected.PaygHourly is null)
        {
            var candidates = cached.Where(PriceSelectors.IsConsumption).ToList();
            var ctx = new Dictionary<string, object?>
            {
                ["sku_name"] = skuTier,
                ["sku_capacity"] = skuCapacity,
                ["expected_product"] = "Azure Cache for Redis or Azure Managed Redis",
                ["expected_unit"] = "hourly cache capacity",
            };
            var payg = AssistSelect("redis", "payg_compute_hourly", ctx, candidates);
            if (payg is not null)
                selected = selected with
                {
                    PaygHourly = payg.RetailPrice,
                    MatchStrategy = payg.AiMatchStrategy,
                    MatchConfidence = payg.AiMatchConfidence,
                    PaygMeterId = payg.MeterId,
                };
        }

        return selected;
    }

    /// <summary>
    /// Python: <c>_select_redis_prices(cached, sku_name, sku_capacity)</c>.
    /// </summary>
    internal static RedisPrices SelectRedisPrices(
        IReadOnlyList<PriceRow> cached, string skuName, int skuCapacity)
    {
        var skuLower = (skuName ?? string.Empty).ToLowerInvariant();
        // classic_sku = f"c{cap}" si sku in {basic, standard} else f"p{cap}".
        var classicSku = (skuLower == "basic" || skuLower == "standard")
            ? $"c{skuCapacity}"
            : $"p{skuCapacity}";

        var baseRows = cached.Where(c =>
        {
            var productLower = (c.ProductName ?? string.Empty).ToLowerInvariant();
            var skuNameLower = (c.SkuName ?? string.Empty).ToLowerInvariant();
            return productLower.Contains(skuLower)
                || skuNameLower == classicSku
                || skuNameLower.Contains(skuLower);
        }).ToList();

        var capacityMatches = baseRows
            .Where(c => PriceSelectors.RedisMatchesCapacity(c, skuCapacity))
            .ToList();
        if (capacityMatches.Count > 0)
            baseRows = capacityMatches;

        // PAYG: mejor meter horario primario entre los Consumption.
        // DIFERIDO (Fase posterior): cuando payg es null, Python invoca _assistant_select
        // (fallback de IA) sobre los candidatos Consumption. Ese fallback NO se porta en Fase 1
        // (asistente casi siempre apagado en prod). Sin IA, payg null → payg_hourly null.
        var payg = PriceSelectors.PreferPrimaryHourly(
            baseRows.Where(PriceSelectors.IsConsumption).ToList());

        var ri1y = baseRows.FirstOrDefault(c => PriceSelectors.IsReservation(c, "1 Year"));
        var ri3y = baseRows.FirstOrDefault(c => PriceSelectors.IsReservation(c, "3 Years"));

        // Paridad EXACTA con Python _select_redis_prices:
        //   match_strategy = payg.get("ai_match_strategy")   → None en ruta determinista (sin IA)
        //   match_confidence = payg.get("ai_match_confidence") → None en ruta determinista
        // Las filas cacheadas no traen esas claves, así que ambos son null aunque haya payg.
        return new RedisPrices(
            PaygHourly: payg?.RetailPrice,
            Ri1yTotal: ri1y?.RetailPrice,
            Ri3yTotal: ri3y?.RetailPrice,
            MatchStrategy: null,
            MatchConfidence: null,
            PaygMeterId: payg?.MeterId);
    }
}
