namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Port de <c>get_sql_managed_instance_prices</c> / <c>_select_sql_mi_prices</c> de
/// app/pricing/price_repository.py (servicio "sql_mi" del CalculatorRegistry).
///
/// Peculiaridades replicadas 1:1:
///   - Filtro por tier+family vía <see cref="PriceSelectors.SqlMiProductTokens"/>; si ningún
///     producto cumple TODOS los tokens, se cae al universo "SQL Managed Instance" completo.
///   - Exclusión de meters/productos "Backup".
///   - Compute por vCore (meter contiene "vCore"), con zone redundancy on/off; preferencia
///     por el SKU de vCore EXACTO (vcores solicitados) → genérico (1 vCore) → cualquiera.
///   - Precio compute normalizado a $/vCore: si el SKU trae N>1 vCores, se divide entre N.
///   - Storage por meter "Data Stored" filtrado por tokens[0] (tier) + zone redundancy.
///   - RI 1y/3y: primer item Reservation del término correspondiente dentro de "base".
///   - Fallback de IA (_assistant_select) DIFERIDO a una fase posterior: el Python lo invoca
///     para compute, storage, RI 1y y RI 3y cuando el determinista no halla; en Fase 1 ese
///     fallback queda FUERA de alcance (asistente casi siempre apagado en prod), así que si el
///     determinista no halla, el valor queda en null.
/// </summary>
public sealed partial class SqlPriceRepository
{
    public SqlManagedInstancePrices GetSqlManagedInstancePrices(
        string region, string skuTier = "", string skuFamily = "", int vcores = 0, bool zoneRedundant = false)
    {
        const string serviceName = "SQL Managed Instance";
        var fetchQuery = $"sql_mi {region}";

        // Refresh por fetch_query (lookup por región), igual que el original.
        RefreshByFetchQueryIfStale(fetchQuery, () => _client.FetchSqlManagedInstancePrices(region));

        var cached = _cache.QueryCached(serviceName, region);
        return SelectSqlMiPrices(cached, skuTier, skuFamily, vcores, zoneRedundant);
    }

    /// <summary>Port de <c>_select_sql_mi_prices</c>.</summary>
    private static SqlManagedInstancePrices SelectSqlMiPrices(
        IReadOnlyList<PriceRow> cached,
        string skuTier = "",
        string skuFamily = "",
        int vcores = 0,
        bool zoneRedundant = false)
    {
        var tokens = PriceSelectors.SqlMiProductTokens(skuTier, skuFamily);
        var requestedVcores = vcores;

        // Replica zone_matches(item): True si el flag de zona pedido coincide con la presencia
        // de "zone redundancy" en meter_name + sku_name (lower).
        bool ZoneMatches(PriceRow item)
        {
            var haystack = string.Join(" ",
                item.MeterName ?? string.Empty,
                item.SkuName ?? string.Empty).ToLowerInvariant();
            var isZr = haystack.Contains("zone redundancy");
            return zoneRedundant ? isZr : !isZr;
        }

        var allBase = cached
            .Where(c => (c.ProductName ?? string.Empty).Contains("SQL Managed Instance")
                && !PriceSelectors.Contains(c.ProductName, "Backup")
                && !PriceSelectors.Contains(c.MeterName, "Backup"))
            .ToList();

        var matching = allBase
            .Where(c => tokens.All(token =>
                (c.ProductName ?? string.Empty).ToLowerInvariant().Contains(token)))
            .ToList();
        var baseList = matching.Count > 0 ? matching : allBase;

        var computeCandidates = baseList
            .Where(c => PriceSelectors.IsConsumption(c)
                && PriceSelectors.Contains(c.MeterName, "vCore")
                && ZoneMatches(c))
            .ToList();

        PriceRow? compute = null;
        if (requestedVcores > 0)
        {
            var exactCompute = computeCandidates
                .Where(c => PriceSelectors.VcoreCountFromSku(c.SkuName ?? string.Empty) == requestedVcores)
                .ToList();
            compute = PriceSelectors.PreferPrimaryHourly(exactCompute);
        }
        if (compute is null)
        {
            var genericCompute = computeCandidates
                .Where(c => PriceSelectors.VcoreCountFromSku(c.SkuName ?? string.Empty) == 1)
                .ToList();
            compute = PriceSelectors.PreferPrimaryHourly(genericCompute);
        }
        if (compute is null)
        {
            compute = PriceSelectors.PreferPrimaryHourly(computeCandidates);
        }
        // DIFERIDO (Fase posterior): aquí Python invoca _assistant_select para compute. Sin IA
        // (fuera de alcance de Fase 1), si compute sigue null, queda null.

        var storageCandidates = allBase
            .Where(c => PriceSelectors.IsConsumption(c)
                && PriceSelectors.Contains(c.MeterName, "Data Stored")
                && (c.ProductName ?? string.Empty).ToLowerInvariant().Contains(tokens[0])
                && ZoneMatches(c))
            .ToList();
        var storage = PriceSelectors.PreferPrimaryHourly(storageCandidates);
        // DIFERIDO (Fase posterior): Python invoca _assistant_select para storage cuando es null.

        var ri1y = baseList.FirstOrDefault(c => PriceSelectors.IsReservation(c, "1 Year"));
        var ri3y = baseList.FirstOrDefault(c => PriceSelectors.IsReservation(c, "3 Years"));
        // DIFERIDO (Fase posterior): Python invoca _assistant_select para RI 1y/3y cuando son null.

        double? computePrice = compute?.RetailPrice;
        var computeSkuVcores = compute is not null
            ? PriceSelectors.VcoreCountFromSku(compute.SkuName ?? string.Empty)
            : 1;
        if (computePrice is not null && computeSkuVcores > 1)
        {
            computePrice = computePrice.Value / computeSkuVcores;
        }

        return new SqlManagedInstancePrices(
            PaygHourlyPerVcore: computePrice,
            StorageMonthlyPerGb: storage?.RetailPrice,
            Ri1yTotalPerVcore: ri1y?.RetailPrice,
            Ri3yTotalPerVcore: ri3y?.RetailPrice,
            ComputeMeterName: compute?.MeterName,
            ComputeSkuName: compute?.SkuName,
            StorageMeterName: storage?.MeterName,
            StorageSkuName: storage?.SkuName,
            ProductName: compute?.ProductName,
            // Paridad EXACTA con Python _select_sql_mi_prices:
            //   "match_strategy": compute.get("ai_match_strategy") if compute else "deterministic"
            // En ruta determinista (Fase 1) las filas cacheadas NO traen "ai_match_strategy", así
            // que: compute ENCONTRADO → None (null); compute NO encontrado → "deterministic".
            MatchStrategy: compute is not null ? null : "deterministic",
            MatchConfidence: null);
    }
}
