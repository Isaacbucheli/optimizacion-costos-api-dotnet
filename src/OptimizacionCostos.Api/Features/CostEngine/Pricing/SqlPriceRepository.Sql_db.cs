namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Port de <c>get_sql_db_price_details</c> y su selector <c>_select_sql_db_price_item</c>
/// de app/pricing/price_repository.py (servicio "sql_db" del CalculatorRegistry).
///
/// Peculiaridad clave (paridad EXACTA): cuatro buckets de selección que NO deben
/// confundirse, porque cada uno apunta a un meter distinto y por ende a un precio distinto:
///   - Basic        → DTU "B" (product "Single Basic", sku == "b", meter con "DTU").
///   - GP_S_Gen5    → General Purpose SERVERLESS Compute Gen5 (meter "vCore").
///   - HS_S_Gen5    → Hyperscale SERVERLESS Compute Gen5 (meter "vCore").
///   - GP_Gen5      → General Purpose PROVISIONED Compute Gen5 (NO Serverless, meter "vCore").
/// Confundir serverless con provisioned cambia el precio elegido.
///
/// El fallback de IA (_assistant_select) NO se porta en Fase 1: si la selección
/// determinista no encuentra item, devolvemos null (igual que con assist deshabilitado).
/// </summary>
public sealed partial class SqlPriceRepository
{
    public SqlDbPriceDetails? GetSqlDbPriceDetails(
        string skuName, string region, string skuTier = "", string computeTier = "")
    {
        const string serviceName = "SQL Database";

        // mapped_lookup: ¿el lookup va por fetch_query amplio (región completa) o
        // por sku_name específico? Replica la condición del Python 1:1.
        var skuLower = (skuName ?? string.Empty).ToLowerInvariant();
        var mappedLookup =
            skuLower is "basic" or "gp_s_gen5" or "hs_s_gen5"
            || skuLower.StartsWith("gp_gen5", StringComparison.Ordinal)
            || (skuTier ?? string.Empty).ToLowerInvariant() == "generalpurpose"
            || (computeTier ?? string.Empty).ToLowerInvariant() == "serverless";

        var fetchQuery = $"sql_db {skuName} {region}";

        // Refresh si la cache no está fresca. La frescura se evalúa distinto según el modo:
        //   - mapped_lookup  → por fetch_query (lookup amplio por región).
        //   - no mapped      → por servicio+region+sku_name.
        if (mappedLookup)
        {
            // RefreshByFetchQueryIfStale: IsFetchQueryFresh → fetch(region, null) →
            // DeleteByFetchQuery → Insert (capturando excepciones).
            RefreshByFetchQueryIfStale(
                fetchQuery,
                () => _client.FetchSqlDbPrices(region, null));
        }
        else
        {
            // RefreshIfStale por sku_name: IsCacheFreshFor → fetch(region, sku) →
            // DeleteStale → Insert (capturando excepciones).
            RefreshIfStale(
                serviceName,
                region,
                () => _client.FetchSqlDbPrices(region, skuName),
                fetchQuery,
                skuName: skuName);
        }

        // _query_cached(service, region) si mapped; si no, con sku_name.
        var cached = mappedLookup
            ? _cache.QueryCached(serviceName, region)
            : _cache.QueryCached(serviceName, region, skuName: skuName);

        var item = SelectSqlDbPriceItem(
            cached, skuName ?? string.Empty, skuTier ?? string.Empty, computeTier ?? string.Empty);

        if (item is null)
        {
            // Fallback IA: la selección determinista no cubre este caso (p.ej. Hyperscale
            // PROVISIONED, HS_Gen5). Traer TODOS los meters de SQL Database de la región y dejar
            // que la IA elija un meter REAL de cómputo (vCore/DTU, Consumption).
            RefreshByFetchQueryIfStale($"sql_db_all {region}", () => _client.FetchSqlDbPrices(region, null));
            // Curación de candidatos: meters de cómputo (vCore/DTU) Consumption, y si conocemos
            // el tier (Hyperscale/General Purpose/Business Critical) filtramos a ESE tier para que
            // el meter correcto NO quede fuera del tope que ve la IA (evita que elija un meter
            // plausible pero más caro de otra serie, p.ej. DC-Series en vez de Gen5).
            var tierToken = VcorePoolTierTokens.TryGetValue((skuTier ?? string.Empty).ToLowerInvariant(), out var tt)
                ? tt
                : null;
            var broad = _cache.QueryCached(serviceName, region)
                .Where(c => c.IsConsumption
                    && (PriceSelectors.Contains(c.MeterName, "vCore") || PriceSelectors.Contains(c.MeterName, "DTU"))
                    && !PriceSelectors.Contains(c.MeterName, "Free")
                    && !PriceSelectors.Contains(c.MeterName, "Backup")
                    && (tierToken is null || PriceSelectors.Contains(c.ProductName, tierToken)))
                .ToList();
            var ctx = new Dictionary<string, object?>
            {
                ["sku_name"] = skuName,
                ["sku_tier"] = skuTier,
                ["compute_tier"] = computeTier,
                ["region"] = region,
            };
            item = AssistSelect("sql_db", "compute", ctx, broad);
            if (item is null)
                return null;
        }

        return new SqlDbPriceDetails(
            RetailPrice: item.RetailPrice,
            UnitOfMeasure: item.UnitOfMeasure ?? string.Empty,
            MeterName: item.MeterName ?? string.Empty,
            ProductName: item.ProductName ?? string.Empty,
            SkuName: item.SkuName ?? string.Empty,
            MatchStrategy: item.AiMatchStrategy,
            PaygMeterId: item.MeterId);
    }

    /// <summary>
    /// Port de <c>_select_sql_db_price_item</c>. Filtra primero el "base" (Consumption sin
    /// Free/Backup/Zone Redundancy) y luego entra a uno de los buckets según sku/tier/compute,
    /// devolviendo el mejor candidato vía <see cref="PriceSelectors.PreferPrimaryHourly"/>.
    /// </summary>
    internal static PriceRow? SelectSqlDbPriceItem(
        IReadOnlyList<PriceRow> cached, string skuName, string skuTier = "", string computeTier = "")
    {
        var skuLower = (skuName ?? string.Empty).ToLowerInvariant();
        var tierLower = (skuTier ?? string.Empty).ToLowerInvariant();
        var computeLower = (computeTier ?? string.Empty).ToLowerInvariant();

        var baseRows = cached
            .Where(c => PriceSelectors.IsConsumption(c)
                && !PriceSelectors.Contains(c.MeterName, "Free")
                && !PriceSelectors.Contains(c.MeterName, "Backup")
                && !PriceSelectors.Contains(c.ProductName, "Backup")
                && !PriceSelectors.Contains(c.MeterName, "Zone Redundancy")
                && !PriceSelectors.Contains(c.SkuName, "Zone Redundancy"))
            .ToList();

        if (skuLower == "basic" || tierLower == "basic")
        {
            var candidates = baseRows
                .Where(c => PriceSelectors.Contains(c.ProductName, "Single Basic")
                    && (c.SkuName ?? string.Empty).ToLowerInvariant() == "b"
                    && PriceSelectors.Contains(c.MeterName, "DTU")
                    && !PriceSelectors.Contains(c.MeterName, "Secondary"))
                .ToList();
            return PriceSelectors.PreferPrimaryHourly(candidates);
        }

        if (skuLower == "gp_s_gen5" || (tierLower == "generalpurpose" && computeLower == "serverless"))
        {
            var candidates = baseRows
                .Where(c => PriceSelectors.Contains(c.ProductName, "General Purpose")
                    && PriceSelectors.Contains(c.ProductName, "Serverless")
                    && PriceSelectors.Contains(c.ProductName, "Compute Gen5")
                    && PriceSelectors.Contains(c.MeterName, "vCore"))
                .ToList();
            return PriceSelectors.PreferPrimaryHourly(candidates);
        }

        if (skuLower == "hs_s_gen5" || (tierLower == "hyperscale" && computeLower == "serverless"))
        {
            var candidates = baseRows
                .Where(c => PriceSelectors.Contains(c.ProductName, "Hyperscale")
                    && PriceSelectors.Contains(c.ProductName, "Serverless")
                    && PriceSelectors.Contains(c.ProductName, "Compute Gen5")
                    && PriceSelectors.Contains(c.MeterName, "vCore"))
                .ToList();
            return PriceSelectors.PreferPrimaryHourly(candidates);
        }

        // General Purpose Provisioned Compute Gen5 (NO serverless): precio por vCore/hora.
        if (skuLower.StartsWith("gp_gen5", StringComparison.Ordinal)
            || (tierLower == "generalpurpose" && computeLower != "serverless"))
        {
            var candidates = baseRows
                .Where(c => PriceSelectors.Contains(c.ProductName, "General Purpose")
                    && PriceSelectors.Contains(c.ProductName, "Compute Gen5")
                    && !PriceSelectors.Contains(c.ProductName, "Serverless")
                    && PriceSelectors.Contains(c.MeterName, "vCore"))
                .ToList();
            return PriceSelectors.PreferPrimaryHourly(candidates);
        }

        var direct = baseRows
            .Where(c => (c.SkuName ?? string.Empty).ToLowerInvariant() == skuLower)
            .ToList();
        return PriceSelectors.PreferPrimaryHourly(direct);
    }
}
