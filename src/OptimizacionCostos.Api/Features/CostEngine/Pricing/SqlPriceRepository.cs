namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Implementación de <see cref="IPriceRepository"/>. Port de las funciones públicas
/// <c>get_*</c> de app/pricing/price_repository.py.
///
/// ESQUELETO (Fase 1): aquí van solo el ctor, los campos compartidos y helpers privados
/// transversales. Los métodos de <see cref="IPriceRepository"/> (GetVmPrices,
/// GetDiskPrice, GetAppServicePrices, ...) los implementan agentes por servicio en
/// archivos PARCIALES (ej. SqlPriceRepository.Vm.cs, SqlPriceRepository.AppService.cs).
/// Por eso la clase es <c>partial</c> y NO declara aún esos métodos: cada archivo parcial
/// agrega su(s) método(s) de servicio. Mientras no estén todos, el proyecto API compila
/// (la interfaz se satisface al juntar todos los parciales).
///
/// Dependencias (inyectadas):
///   - <see cref="IPriceCache"/>: cache en dbo.azure_retail_prices (query/fresh/insert/delete).
///   - <see cref="IRetailPriceClient"/>: cliente HTTP de Azure Retail Prices (fetch_*).
///   - <see cref="IPricingConstants"/>: horas/mes, add-ons (lo usan algunas calculadoras;
///     se inyecta aquí por conveniencia para los parciales que lo necesiten).
///
/// Patrón de cada get_* (igual que Python): si la cache no está fresca, intenta fetch +
/// delete + insert (capturando excepciones y siguiendo con la cache vieja), luego
/// consulta la cache y delega la selección en los selectores de <see cref="PriceSelectors"/>.
/// </summary>
public sealed partial class SqlPriceRepository : IPriceRepository
{
    private readonly IPriceCache _cache;
    private readonly IRetailPriceClient _client;
    private readonly IPricingConstants _constants;
    private readonly IPriceAssistant? _assistant;

    // El asistente IA es OPCIONAL (default null) para no romper construcciones existentes
    // (tests de paridad) ni cambiar el comportamiento cuando no se inyecta. DI lo provee.
    public SqlPriceRepository(
        IPriceCache cache,
        IRetailPriceClient client,
        IPricingConstants constants,
        IPriceAssistant? assistant = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _constants = constants ?? throw new ArgumentNullException(nameof(constants));
        _assistant = assistant;
    }

    /// <summary>
    /// Fallback IA compartido: si el asistente está activo y hay candidatos REALES, deja que
    /// la IA elija uno. Devuelve la <see cref="PriceRow"/> elegida (marcada con AiMatch*) o null.
    /// Lo invocan los get_* cuando la selección determinista no encuentra precio.
    /// </summary>
    private PriceRow? AssistSelect(
        string serviceKey,
        string component,
        IReadOnlyDictionary<string, object?> context,
        IReadOnlyList<PriceRow> candidates)
        => _assistant is { IsEnabled: true } && candidates.Count > 0
            ? _assistant.SelectCandidate(serviceKey, component, context, candidates)
            : null;

    // -------------------- Specs compartidas --------------------

    /// <summary>
    /// Python: <c>ELASTIC_PREMIUM_SPECS</c>. (vCPU, GiB RAM) por SKU de planes Elastic Premium
    /// (Functions EP*) y Workflow Standard (Logic Apps WS*). Lo usa el parcial de App Service /
    /// Elastic Premium. Clave en MAYÚSCULAS.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (int Vcpu, double Gb)> ElasticPremiumSpecs =
        new Dictionary<string, (int, double)>(StringComparer.Ordinal)
        {
            ["EP1"] = (1, 3.5), ["EP2"] = (2, 7.0), ["EP3"] = (4, 14.0),
            ["WS1"] = (1, 3.5), ["WS2"] = (2, 7.0), ["WS3"] = (4, 14.0),
        };

    /// <summary>
    /// Python: <c>DTU_POOL_TIERS</c>. tier(lower) → nombre de producto DTU del Elastic Pool.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> DtuPoolTiers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["basic"] = "Basic", ["standard"] = "Standard", ["premium"] = "Premium",
        };

    /// <summary>
    /// Python: <c>VCORE_POOL_TIER_TOKENS</c>. tier(lower) → token de producto vCore del Elastic Pool.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> VcorePoolTierTokens =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["generalpurpose"] = "General Purpose",
            ["businesscritical"] = "Business Critical",
            ["hyperscale"] = "Hyperscale",
        };

    // -------------------- Helpers privados compartidos --------------------

    /// <summary>
    /// Patrón "refresh si rancio" parametrizado por sku_name/arm_sku_name, usado por los
    /// get_* que cachean por servicio+region+sku (VM, Disk, App Service). Si la cache no está
    /// fresca, ejecuta <paramref name="fetch"/>, borra lo viejo (DeleteStale) e inserta lo nuevo,
    /// capturando cualquier excepción (igual que Python: loguea y sigue con la cache vieja).
    /// </summary>
    private void RefreshIfStale(
        string serviceName,
        string region,
        Func<IReadOnlyList<PriceRow>> fetch,
        string fetchQuery,
        string? armSkuName = null,
        string? skuName = null)
    {
        if (_cache.IsCacheFreshFor(serviceName, region, armSkuName, skuName))
            return;
        try
        {
            var items = fetch();
            _cache.DeleteStale(serviceName, region, armSkuName, skuName);
            _cache.Insert(items, fetchQuery);
        }
        catch
        {
            // Paridad con Python: el fetch falló (red/HTTP); se sigue con lo que haya en cache.
        }
    }

    /// <summary>
    /// Variante de refresh por fetch_query (lookups amplios / por región: MySQL, Redis, Synapse,
    /// Elastic Pool, Public IP, fallbacks). Si el fetch_query no está fresco, ejecuta
    /// <paramref name="fetch"/>, borra por fetch_query e inserta, capturando excepciones.
    /// </summary>
    private void RefreshByFetchQueryIfStale(string fetchQuery, Func<IReadOnlyList<PriceRow>> fetch)
    {
        if (_cache.IsFetchQueryFresh(fetchQuery))
            return;
        try
        {
            var items = fetch();
            _cache.DeleteByFetchQuery(fetchQuery);
            _cache.Insert(items, fetchQuery);
        }
        catch
        {
            // Paridad con Python: el fetch falló; se sigue con lo que haya en cache.
        }
    }
}
