using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Doble de prueba de <see cref="IPriceCache"/>. Replica los <c>patch.object(prices,
/// "_query_cached", ...)</c> / <c>"_is_cache_fresh_for"</c> / <c>"_is_fetch_query_fresh"</c>
/// de los tests Python, y registra las llamadas a Insert / DeleteStale /
/// DeleteByFetchQuery para aserciones estilo <c>mock.assert_called_once_with(...)</c>.
///
/// Defaults:
///   - <see cref="QueryCachedFn"/>: lista vacía.
///   - <see cref="IsCacheFreshForFn"/> / <see cref="IsFetchQueryFreshFn"/>: true (cache fresca,
///     o sea NO se dispara el fetch, igual que cuando el test parchea _is_*_fresh=True).
///
/// Uso típico (return_value):
/// <code>
///   var cache = new FakePriceCache { IsFetchQueryFreshFn = _ => false };
///   cache.QueryCachedFn = (_, _, _, _) => myRows;
/// </code>
///
/// Uso con side_effect (varias respuestas en orden, como side_effect=[a, b] de Python):
/// <code>
///   cache.QueueCached(initialCache, refreshedCache); // 1ra llamada→initial, 2da→refreshed
/// </code>
/// </summary>
public sealed class FakePriceCache : IPriceCache
{
    public Func<string, string, string?, string?, IReadOnlyList<PriceRow>> QueryCachedFn { get; set; }
        = (_, _, _, _) => Array.Empty<PriceRow>();

    public Func<string, string, string?, string?, bool> IsCacheFreshForFn { get; set; }
        = (_, _, _, _) => true;

    public Func<string, bool> IsFetchQueryFreshFn { get; set; }
        = _ => true;

    // -------------------- Registro de llamadas (para aserciones) --------------------

    public List<(IReadOnlyList<PriceRow> Rows, string FetchQuery)> InsertCalls { get; } = new();
    public List<(string ServiceName, string Region, string? ArmSkuName, string? SkuName)> DeleteStaleCalls { get; } = new();
    public List<string> DeleteByFetchQueryCalls { get; } = new();
    public List<(string ServiceName, string Region, string? ArmSkuName, string? SkuName)> QueryCachedCalls { get; } = new();
    public List<(string ServiceName, string Region, string? ArmSkuName, string? SkuName)> IsCacheFreshForCalls { get; } = new();
    public List<string> IsFetchQueryFreshCalls { get; } = new();

    public int InsertCallCount => InsertCalls.Count;
    public int DeleteStaleCallCount => DeleteStaleCalls.Count;
    public int DeleteByFetchQueryCallCount => DeleteByFetchQueryCalls.Count;
    public int QueryCachedCallCount => QueryCachedCalls.Count;

    /// <summary>
    /// Encola respuestas para <see cref="QueryCached"/> en orden (equivale a
    /// <c>side_effect=[a, b, ...]</c> de Python). La última se reusa si hay más llamadas.
    /// </summary>
    public void QueueCached(params IReadOnlyList<PriceRow>[] responses)
    {
        var queue = new Queue<IReadOnlyList<PriceRow>>(responses);
        IReadOnlyList<PriceRow> last = responses.Length > 0 ? responses[^1] : Array.Empty<PriceRow>();
        QueryCachedFn = (_, _, _, _) => queue.Count > 0 ? queue.Dequeue() : last;
    }

    // -------------------- IPriceCache --------------------

    public IReadOnlyList<PriceRow> QueryCached(
        string serviceName, string region, string? armSkuName = null, string? skuName = null)
    {
        QueryCachedCalls.Add((serviceName, region, armSkuName, skuName));
        return QueryCachedFn(serviceName, region, armSkuName, skuName);
    }

    public bool IsCacheFreshFor(
        string serviceName, string region, string? armSkuName = null, string? skuName = null)
    {
        IsCacheFreshForCalls.Add((serviceName, region, armSkuName, skuName));
        return IsCacheFreshForFn(serviceName, region, armSkuName, skuName);
    }

    public bool IsFetchQueryFresh(string fetchQuery)
    {
        IsFetchQueryFreshCalls.Add(fetchQuery);
        return IsFetchQueryFreshFn(fetchQuery);
    }

    public void Insert(IEnumerable<PriceRow> rows, string fetchQuery)
        => InsertCalls.Add((rows.ToList(), fetchQuery));

    public void DeleteStale(
        string serviceName, string region, string? armSkuName = null, string? skuName = null)
        => DeleteStaleCalls.Add((serviceName, region, armSkuName, skuName));

    public void DeleteByFetchQuery(string fetchQuery)
        => DeleteByFetchQueryCalls.Add(fetchQuery);

    // -------------------- Fase 3: clear-all / status --------------------

    /// <summary>Filas a "borrar" que devuelve <see cref="ClearAll"/> (configurable).</summary>
    public int ClearAllReturns { get; set; }
    public int ClearAllCallCount { get; private set; }

    /// <summary>Resumen que devuelve <see cref="GetStatus"/> (configurable).</summary>
    public IReadOnlyList<OptimizacionCostos.Api.Features.CostEngine.Api.PriceCacheStatusRow> StatusReturns { get; set; }
        = Array.Empty<OptimizacionCostos.Api.Features.CostEngine.Api.PriceCacheStatusRow>();
    public int GetStatusCallCount { get; private set; }

    public int ClearAll()
    {
        ClearAllCallCount++;
        return ClearAllReturns;
    }

    public IReadOnlyList<OptimizacionCostos.Api.Features.CostEngine.Api.PriceCacheStatusRow> GetStatus()
    {
        GetStatusCallCount++;
        return StatusReturns;
    }
}
