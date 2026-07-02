namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Precios de MySQL Flexible Server (servicio "mysql" del CalculatorRegistry).
/// Port 1:1 de get_mysql_flex_prices / get_mysql_storage_price_per_gb y de sus selectores
/// _select_mysql_flex_prices / _select_mysql_storage_price en
/// app/pricing/price_repository.py.
///
/// Peculiaridades portadas:
///   - Ambos métodos cachean por FETCH_QUERY (no por servicio+región+sku): usan
///     RefreshByFetchQueryIfStale con $"mysql {armSku} {region}" / $"mysql_storage {region}",
///     pero la consulta cacheada se hace por servicio+región ("Azure Database for MySQL").
///   - Selección de compute filtra por serie (MySqlSeriesTokens), exige Consumption +
///     (vCore | Hour | meter que empieza con "B"), y excluye Storage/Backup/IOPS.
///   - is_per_vcore = "vcore" en el meter del compute elegido.
///   - El fallback de IA (_assistant_select) NO se porta en Fase 1: si el determinista no
///     halla, se devuelve null (igual que con el asistente deshabilitado).
/// </summary>
public sealed partial class SqlPriceRepository
{
    // Service name compartido por ambos métodos (igual que Python).
    private const string MySqlServiceName = "Azure Database for MySQL";

    /// <summary>
    /// Python: <c>get_mysql_flex_prices(arm_sku_name, region, sku_tier)</c>.
    /// </summary>
    public MySqlFlexPrices GetMySqlFlexPrices(string armSkuName, string region, string skuTier = "")
    {
        var fetchQuery = $"mysql {armSkuName} {region}";
        RefreshByFetchQueryIfStale(fetchQuery, () => _client.FetchMySqlFlexPrices(armSkuName, region));

        var cached = _cache.QueryCached(MySqlServiceName, region);
        var selected = SelectMySqlFlexPrices(cached, armSkuName, skuTier);

        // Fallback IA (paridad con Python _select_mysql_flex_prices): si el determinista no halló
        // compute, la IA elige un meter REAL de cómputo entre los Consumption de "Flexible Server"
        // (serie-filtrados), excluyendo Storage/Backup/IOPS.
        if (selected.PaygHourly is null)
        {
            var baseItems = MySqlFlexBase(cached, armSkuName, skuTier);
            var candidates = baseItems
                .Where(c => PriceSelectors.IsConsumption(c)
                    && !PriceSelectors.Contains(c.MeterName, "Storage")
                    && !PriceSelectors.Contains(c.MeterName, "Backup")
                    && !PriceSelectors.Contains(c.MeterName, "IOPS"))
                .ToList();
            var ctx = new Dictionary<string, object?>
            {
                ["sku_name"] = armSkuName,
                ["sku_tier"] = skuTier,
                ["expected_product"] = "Azure Database for MySQL Flexible Server",
                ["expected_unit"] = "hourly compute",
            };
            var compute = AssistSelect("mysql", "payg_compute_hourly", ctx, candidates);
            if (compute is not null)
                selected = selected with
                {
                    PaygHourly = compute.RetailPrice,
                    IsPerVcore = (compute.MeterName ?? string.Empty).ToLowerInvariant().Contains("vcore"),
                    MatchStrategy = compute.AiMatchStrategy,
                    MatchConfidence = compute.AiMatchConfidence,
                    PaygMeterId = compute.MeterId,
                };
        }

        return selected;
    }

    /// <summary>
    /// Python: <c>get_mysql_storage_price_per_gb(region)</c>.
    /// </summary>
    public double? GetMySqlStoragePricePerGb(string region)
    {
        var fetchQuery = $"mysql_storage {region}";
        RefreshByFetchQueryIfStale(fetchQuery, () => _client.FetchMySqlStoragePrice(region));

        var cached = _cache.QueryCached(MySqlServiceName, region);
        var storage = SelectMySqlStoragePrice(cached);
        if (storage is not null)
            return storage;

        // Fallback IA (paridad con Python _select_mysql_storage_price): la IA elige un meter REAL
        // de almacenamiento entre los Consumption de "Flexible Server".
        var candidates = cached
            .Where(c => (c.ProductName ?? string.Empty).Contains("Flexible Server")
                && PriceSelectors.IsConsumption(c))
            .ToList();
        var ctx = new Dictionary<string, object?>
        {
            ["expected_product"] = "Azure Database for MySQL Flexible Server",
            ["expected_unit"] = "GB per month storage",
        };
        var picked = AssistSelect("mysql", "storage_monthly_per_gb", ctx, candidates);
        return picked?.RetailPrice;
    }

    /// <summary>
    /// Base serie-filtrada de MySQL Flexible Server (port del bloque inicial de
    /// <see cref="SelectMySqlFlexPrices"/>): filas "Flexible Server" y, si hay tokens de serie con
    /// coincidencias, restringidas a esa serie. Compartida por el selector determinista y el
    /// fallback de IA para garantizar que ambos parten del MISMO universo de candidatos.
    /// </summary>
    internal static List<PriceRow> MySqlFlexBase(
        IReadOnlyList<PriceRow> cached, string skuName = "", string skuTier = "")
    {
        var baseItems = cached
            .Where(c => (c.ProductName ?? string.Empty).Contains("Flexible Server"))
            .ToList();

        var tokens = PriceSelectors.MySqlSeriesTokens(skuName, skuTier)
            .Select(t => t.ToLowerInvariant())
            .ToList();
        if (tokens.Count > 0)
        {
            var matchingSeries = baseItems
                .Where(c =>
                {
                    var haystack = string.Join(" ",
                        c.ProductName ?? string.Empty,
                        c.SkuName ?? string.Empty,
                        c.ArmSkuName ?? string.Empty,
                        c.MeterName ?? string.Empty).ToLowerInvariant();
                    return tokens.All(haystack.Contains);
                })
                .ToList();
            if (matchingSeries.Count > 0)
                baseItems = matchingSeries;
        }

        return baseItems;
    }

    // -------------------- Selectores deterministas (port de los _select_*) --------------------

    /// <summary>
    /// Python: <c>_select_mysql_flex_prices(cached, sku_name, sku_tier)</c>.
    /// </summary>
    private static MySqlFlexPrices SelectMySqlFlexPrices(
        IReadOnlyList<PriceRow> cached, string skuName = "", string skuTier = "")
    {
        // base serie-filtrada (compartida con el fallback de IA, ver MySqlFlexBase).
        var baseItems = MySqlFlexBase(cached, skuName, skuTier);

        // compute: Consumption + (meter contiene "vCore" | unit contiene "Hour" | meter empieza con "B") + no Storage/Backup/IOPS.
        var compute = PriceSelectors.PreferPrimaryHourly(
            baseItems
                .Where(c => PriceSelectors.IsConsumption(c)
                    && (PriceSelectors.Contains(c.MeterName, "vCore")
                        || PriceSelectors.Contains(c.UnitOfMeasure, "Hour")
                        || (c.MeterName ?? string.Empty).ToUpperInvariant().StartsWith("B"))
                    && !PriceSelectors.Contains(c.MeterName, "Storage")
                    && !PriceSelectors.Contains(c.MeterName, "Backup")
                    && !PriceSelectors.Contains(c.MeterName, "IOPS"))
                .ToList());
        // DIFERIDO (Fase posterior): cuando compute es null, Python invoca _assistant_select
        // (fallback de IA) sobre los candidatos Consumption. Ese fallback NO se porta en Fase 1
        // (el asistente está casi siempre apagado en prod). Sin IA, compute null → payg null.

        var ri1y = baseItems.FirstOrDefault(c => PriceSelectors.IsReservation(c, "1 Year"));
        var ri3y = baseItems.FirstOrDefault(c => PriceSelectors.IsReservation(c, "3 Years"));

        // Paridad EXACTA con Python _select_mysql_flex_prices:
        //   "match_strategy": compute.get("ai_match_strategy") if compute else "deterministic"
        // En la ruta determinista (Fase 1) las filas cacheadas NO traen "ai_match_strategy",
        // así que: compute ENCONTRADO → None (null); compute NO encontrado → "deterministic".
        return new MySqlFlexPrices(
            PaygHourly: compute?.RetailPrice,
            Ri1yTotal: ri1y?.RetailPrice,
            Ri3yTotal: ri3y?.RetailPrice,
            IsPerVcore: compute is not null
                && (compute.MeterName ?? string.Empty).ToLowerInvariant().Contains("vcore"),
            MatchStrategy: compute is not null ? null : "deterministic",
            MatchConfidence: null,
            PaygMeterId: compute?.MeterId);
    }

    /// <summary>
    /// Python: <c>_select_mysql_storage_price(cached)</c>.
    /// </summary>
    private static double? SelectMySqlStoragePrice(IReadOnlyList<PriceRow> cached)
    {
        var storage = PriceSelectors.PreferPrimaryHourly(
            cached
                .Where(c => (c.ProductName ?? string.Empty).Contains("Flexible Server")
                    && PriceSelectors.Contains(c.MeterName, "Storage")
                    && PriceSelectors.IsConsumption(c))
                .ToList());
        // (Fase 1: sin fallback IA → null si el determinista no halla.)
        return storage?.RetailPrice;
    }
}
