using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Doble de prueba de <see cref="IPriceRepository"/>. Cada método delega en un
/// <c>Func&lt;...&gt;</c> público y mutable que el test puede reasignar, imitando
/// <c>patch.object(prices, "get_X", return_value=...)</c> / <c>side_effect=...</c> de Python.
///
/// Uso típico (estilo return_value):
/// <code>
///   var prices = new FakePriceRepository();
///   prices.GetAppServicePricesFn = (_, _, _) => new AppServicePrices(0.20, 1200.0, 2700.0);
/// </code>
///
/// Uso con side_effect (lógica según argumentos):
/// <code>
///   prices.GetRedisPricesFn = (sku, _, _) => sku == "Basic"
///       ? new RedisPrices(0.01, null, null)
///       : new RedisPrices(0.10, 500.0, 1200.0);
/// </code>
///
/// Defaults: los métodos que devuelven un tipo anulable (double?, DTO?) devuelven null
/// (== None en Python); los que devuelven un DTO no anulable devuelven un DTO con todos
/// sus precios en null (equivale al dict de None que producen los selectores cuando no
/// hay match).
/// </summary>
public sealed class FakePriceRepository : IPriceRepository
{
    public Func<string, string, string, VmPrices> GetVmPricesFn { get; set; }
        = (_, _, _) => new VmPrices(null, null, null);

    public Func<string, string, double?> GetDiskPriceFn { get; set; }
        = (_, _) => null;

    public Func<string, string, string, double?> GetPublicIpMonthlyPriceFn { get; set; }
        = (_, _, _) => null;

    public Func<string, string, ElasticPremiumBase?> GetElasticPremiumBasePriceFn { get; set; }
        = (_, _) => null;

    public Func<string, string, bool, AppServicePrices> GetAppServicePricesFn { get; set; }
        = (_, _, _) => new AppServicePrices(null, null, null);

    public Func<string, string, string, MySqlFlexPrices> GetMySqlFlexPricesFn { get; set; }
        = (_, _, _) => new MySqlFlexPrices(null, null, null);

    public Func<string, double?> GetMySqlStoragePricePerGbFn { get; set; }
        = _ => null;

    public Func<string, int, string, RedisPrices> GetRedisPricesFn { get; set; }
        = (_, _, _) => new RedisPrices(null, null, null);

    public Func<string, string, string, string, SqlDbPriceDetails?> GetSqlDbPriceDetailsFn { get; set; }
        = (_, _, _, _) => null;

    public Func<string, string, string, int, bool, SqlManagedInstancePrices> GetSqlManagedInstancePricesFn { get; set; }
        = (_, _, _, _, _) => new SqlManagedInstancePrices(null, null, null, null);

    public Func<string, string, SynapseDwPrices> GetSynapseDwPricesFn { get; set; }
        = (_, _) => new SynapseDwPrices(null, null, null);

    public Func<string, string, int, string, ElasticPoolPrices?> GetElasticPoolPricesFn { get; set; }
        = (_, _, _, _) => null;

    // -------------------- IPriceRepository --------------------

    public VmPrices GetVmPrices(string armSkuName, string region, string osType)
        => GetVmPricesFn(armSkuName, region, osType);

    public double? GetDiskPrice(string skuTier, string region)
        => GetDiskPriceFn(skuTier, region);

    public double? GetPublicIpMonthlyPrice(string skuName, string region, string allocationMethod = "Static")
        => GetPublicIpMonthlyPriceFn(skuName, region, allocationMethod);

    public ElasticPremiumBase? GetElasticPremiumBasePrice(string skuName, string region)
        => GetElasticPremiumBasePriceFn(skuName, region);

    public AppServicePrices GetAppServicePrices(string armSkuName, string region, bool isLinux)
        => GetAppServicePricesFn(armSkuName, region, isLinux);

    public MySqlFlexPrices GetMySqlFlexPrices(string armSkuName, string region, string skuTier = "")
        => GetMySqlFlexPricesFn(armSkuName, region, skuTier);

    public double? GetMySqlStoragePricePerGb(string region)
        => GetMySqlStoragePricePerGbFn(region);

    public RedisPrices GetRedisPrices(string skuTier, int skuCapacity, string region)
        => GetRedisPricesFn(skuTier, skuCapacity, region);

    public SqlDbPriceDetails? GetSqlDbPriceDetails(string skuName, string region, string skuTier = "", string computeTier = "")
        => GetSqlDbPriceDetailsFn(skuName, region, skuTier, computeTier);

    public SqlManagedInstancePrices GetSqlManagedInstancePrices(
        string region, string skuTier = "", string skuFamily = "", int vcores = 0, bool zoneRedundant = false)
        => GetSqlManagedInstancePricesFn(region, skuTier, skuFamily, vcores, zoneRedundant);

    public SynapseDwPrices GetSynapseDwPrices(string dwLevel, string region)
        => GetSynapseDwPricesFn(dwLevel, region);

    public ElasticPoolPrices? GetElasticPoolPrices(string skuTier, string skuFamily, int capacity, string region)
        => GetElasticPoolPricesFn(skuTier, skuFamily, capacity, region);
}
