namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Caché de precios en dbo.azure_retail_prices. Port de las funciones de cache de
/// app/pricing/price_repository.py (<c>_query_cached</c>, <c>_is_cache_fresh_for</c>,
/// <c>_is_fetch_query_fresh</c>, <c>_insert_prices_batch</c>, <c>_delete_stale_prices</c>,
/// <c>_delete_prices_by_fetch_query</c>).
///
/// La implementación SQL real es de otra fase; aquí solo el contrato que consumen los
/// métodos get_* de <see cref="SqlPriceRepository"/>. TTL de freshness = 24h
/// (CACHE_TTL_HOURS en Python).
///
/// Notas de paridad:
///   - Las consultas filtran por service + region y opcionalmente arm_sku_name (VMs) o
///     sku_name (Disks/App Service). Los parámetros opcionales null == "no filtrar por
///     ese campo" (igual que los <c>Optional[str] = None</c> de Python).
///   - SQL siempre parametrizado en la implementación.
/// </summary>
public interface IPriceCache
{
    /// <summary>
    /// Python: <c>_query_cached(service_name, region, arm_sku_name=None, sku_name=None)</c>.
    /// Devuelve las filas cacheadas que matchean service + region (+ arm_sku_name / sku_name
    /// si se dan). Lista vacía si no hay nada.
    /// </summary>
    IReadOnlyList<PriceRow> QueryCached(
        string serviceName,
        string region,
        string? armSkuName = null,
        string? skuName = null);

    /// <summary>
    /// Python: <c>_is_cache_fresh_for(service_name, region, arm_sku_name=None, sku_name=None)</c>.
    /// True si el MAX(cached_at) del filtro está dentro del TTL (24h).
    /// </summary>
    bool IsCacheFreshFor(
        string serviceName,
        string region,
        string? armSkuName = null,
        string? skuName = null);

    /// <summary>
    /// Python: <c>_is_fetch_query_fresh(fetch_query)</c>. True si el MAX(cached_at) de las filas
    /// con ese fetch_query está dentro del TTL (24h).
    /// </summary>
    bool IsFetchQueryFresh(string fetchQuery);

    /// <summary>
    /// Python: <c>_insert_prices_batch(items, fetch_query)</c>. Inserta las filas con la etiqueta
    /// <paramref name="fetchQuery"/>. No-op si la colección está vacía.
    /// </summary>
    void Insert(IEnumerable<PriceRow> rows, string fetchQuery);

    /// <summary>
    /// Python: <c>_delete_stale_prices(service_name, region, arm_sku_name=None, sku_name=None)</c>.
    /// Borra las filas del filtro (service + region [+ arm_sku_name] [+ sku_name]) antes de
    /// reinsertar el fetch fresco.
    /// </summary>
    void DeleteStale(
        string serviceName,
        string region,
        string? armSkuName = null,
        string? skuName = null);

    /// <summary>
    /// Python: <c>_delete_prices_by_fetch_query(fetch_query)</c>. Borra todas las filas con ese
    /// fetch_query (usado por los lookups amplios / por región).
    /// </summary>
    void DeleteByFetchQuery(string fetchQuery);

    /// <summary>
    /// Python: <c>clear_all_cache()</c>. Borra TODAS las filas del cache de precios y
    /// devuelve la cantidad de filas eliminadas.
    /// </summary>
    int ClearAll();

    /// <summary>
    /// Python: <c>get_cache_status()</c>. Resumen del cache agrupado por service_name + region:
    /// count, oldest/newest cached_at y si el mas reciente está fresco (&lt; 24h).
    /// </summary>
    IReadOnlyList<Api.PriceCacheStatusRow> GetStatus();
}
