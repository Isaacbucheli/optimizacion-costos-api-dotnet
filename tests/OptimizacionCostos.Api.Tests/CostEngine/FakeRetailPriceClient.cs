using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Doble de prueba de <see cref="IRetailPriceClient"/>. Cada <c>FetchX</c> delega en un
/// <c>Func</c> público y mutable (default: lista vacía), imitando
/// <c>patch.object(prices.api, "fetch_X", return_value=... / side_effect=...)</c>.
/// Registra cada llamada con sus argumentos para aserciones estilo
/// <c>fetch_X.assert_called_once_with(...)</c>.
///
/// Uso típico:
/// <code>
///   var client = new FakeRetailPriceClient();
///   client.FetchRedisPricesFn = (sku, region) => new[] { row };
///   // ... ejercitar el repo ...
///   var call = Assert.Single(client.FetchRedisPricesCalls);
///   Assert.Equal(("Standard", "eastus"), call);
/// </code>
/// </summary>
public sealed class FakeRetailPriceClient : IRetailPriceClient
{
    // -------------------- Funcs configurables --------------------

    public Func<IReadOnlyList<string>, string, string?, IReadOnlyList<PriceRow>> FetchVmPricesFn { get; set; }
        = (_, _, _) => Array.Empty<PriceRow>();
    public Func<string, IReadOnlyList<PriceRow>> FetchVmRegionPricesFn { get; set; } = _ => Array.Empty<PriceRow>();
    public Func<string, string, IReadOnlyList<PriceRow>> FetchManagedDiskPricesFn { get; set; } = (_, _) => Array.Empty<PriceRow>();
    public Func<string, string?, IReadOnlyList<PriceRow>> FetchSqlDbPricesFn { get; set; } = (_, _) => Array.Empty<PriceRow>();
    public Func<string, IReadOnlyList<PriceRow>> FetchElasticPoolPricesFn { get; set; } = _ => Array.Empty<PriceRow>();
    public Func<string, IReadOnlyList<PriceRow>> FetchSynapseDwPricesFn { get; set; } = _ => Array.Empty<PriceRow>();
    public Func<string, IReadOnlyList<PriceRow>> FetchSqlManagedInstancePricesFn { get; set; } = _ => Array.Empty<PriceRow>();
    public Func<string, string, bool, IReadOnlyList<PriceRow>> FetchAppServicePricesFn { get; set; } = (_, _, _) => Array.Empty<PriceRow>();
    public Func<string, IReadOnlyList<PriceRow>> FetchAppServiceRegionPricesFn { get; set; } = _ => Array.Empty<PriceRow>();
    public Func<string, IReadOnlyList<PriceRow>> FetchFunctionsPremiumPricesFn { get; set; } = _ => Array.Empty<PriceRow>();
    public Func<string, IReadOnlyList<PriceRow>> FetchLogicAppsStandardPricesFn { get; set; } = _ => Array.Empty<PriceRow>();
    public Func<string, string, IReadOnlyList<PriceRow>> FetchMySqlFlexPricesFn { get; set; } = (_, _) => Array.Empty<PriceRow>();
    public Func<string, IReadOnlyList<PriceRow>> FetchMySqlStoragePriceFn { get; set; } = _ => Array.Empty<PriceRow>();
    public Func<string, IReadOnlyList<PriceRow>> FetchCosmosPricesFn { get; set; } = _ => Array.Empty<PriceRow>();
    public Func<string, string, IReadOnlyList<PriceRow>> FetchRedisPricesFn { get; set; } = (_, _) => Array.Empty<PriceRow>();
    public Func<string, IReadOnlyList<PriceRow>> FetchAzureManagedRedisPricesFn { get; set; } = _ => Array.Empty<PriceRow>();
    public Func<string, IReadOnlyList<PriceRow>> FetchPublicIpPricesFn { get; set; } = _ => Array.Empty<PriceRow>();
    public Func<string, IReadOnlyList<PriceRow>> FetchSnapshotPricesFn { get; set; } = _ => Array.Empty<PriceRow>();

    // -------------------- Registro de llamadas --------------------

    public List<(IReadOnlyList<string> ArmSkuNames, string Region, string? OsType)> FetchVmPricesCalls { get; } = new();
    public List<string> FetchVmRegionPricesCalls { get; } = new();
    public List<(string SkuName, string Region)> FetchManagedDiskPricesCalls { get; } = new();
    public List<(string Region, string? SkuName)> FetchSqlDbPricesCalls { get; } = new();
    public List<string> FetchElasticPoolPricesCalls { get; } = new();
    public List<string> FetchSynapseDwPricesCalls { get; } = new();
    public List<string> FetchSqlManagedInstancePricesCalls { get; } = new();
    public List<(string SkuName, string Region, bool IsLinux)> FetchAppServicePricesCalls { get; } = new();
    public List<string> FetchAppServiceRegionPricesCalls { get; } = new();
    public List<string> FetchFunctionsPremiumPricesCalls { get; } = new();
    public List<string> FetchLogicAppsStandardPricesCalls { get; } = new();
    public List<(string SkuName, string Region)> FetchMySqlFlexPricesCalls { get; } = new();
    public List<string> FetchMySqlStoragePriceCalls { get; } = new();
    public List<string> FetchCosmosPricesCalls { get; } = new();
    public List<(string SkuName, string Region)> FetchRedisPricesCalls { get; } = new();
    public List<string> FetchAzureManagedRedisPricesCalls { get; } = new();
    public List<string> FetchPublicIpPricesCalls { get; } = new();
    public List<string> FetchSnapshotPricesCalls { get; } = new();

    // -------------------- IRetailPriceClient --------------------

    public IReadOnlyList<PriceRow> FetchVmPrices(IReadOnlyList<string> armSkuNames, string region, string? osType = null)
    {
        FetchVmPricesCalls.Add((armSkuNames, region, osType));
        return FetchVmPricesFn(armSkuNames, region, osType);
    }

    public IReadOnlyList<PriceRow> FetchVmRegionPrices(string region)
    {
        FetchVmRegionPricesCalls.Add(region);
        return FetchVmRegionPricesFn(region);
    }

    public IReadOnlyList<PriceRow> FetchManagedDiskPrices(string skuName, string region)
    {
        FetchManagedDiskPricesCalls.Add((skuName, region));
        return FetchManagedDiskPricesFn(skuName, region);
    }

    public IReadOnlyList<PriceRow> FetchSqlDbPrices(string region, string? skuName = null)
    {
        FetchSqlDbPricesCalls.Add((region, skuName));
        return FetchSqlDbPricesFn(region, skuName);
    }

    public IReadOnlyList<PriceRow> FetchElasticPoolPrices(string region)
    {
        FetchElasticPoolPricesCalls.Add(region);
        return FetchElasticPoolPricesFn(region);
    }

    public IReadOnlyList<PriceRow> FetchSynapseDwPrices(string region)
    {
        FetchSynapseDwPricesCalls.Add(region);
        return FetchSynapseDwPricesFn(region);
    }

    public IReadOnlyList<PriceRow> FetchSqlManagedInstancePrices(string region)
    {
        FetchSqlManagedInstancePricesCalls.Add(region);
        return FetchSqlManagedInstancePricesFn(region);
    }

    public IReadOnlyList<PriceRow> FetchAppServicePrices(string skuName, string region, bool isLinux = false)
    {
        FetchAppServicePricesCalls.Add((skuName, region, isLinux));
        return FetchAppServicePricesFn(skuName, region, isLinux);
    }

    public IReadOnlyList<PriceRow> FetchAppServiceRegionPrices(string region)
    {
        FetchAppServiceRegionPricesCalls.Add(region);
        return FetchAppServiceRegionPricesFn(region);
    }

    public IReadOnlyList<PriceRow> FetchFunctionsPremiumPrices(string region)
    {
        FetchFunctionsPremiumPricesCalls.Add(region);
        return FetchFunctionsPremiumPricesFn(region);
    }

    public IReadOnlyList<PriceRow> FetchLogicAppsStandardPrices(string region)
    {
        FetchLogicAppsStandardPricesCalls.Add(region);
        return FetchLogicAppsStandardPricesFn(region);
    }

    public IReadOnlyList<PriceRow> FetchMySqlFlexPrices(string skuName, string region)
    {
        FetchMySqlFlexPricesCalls.Add((skuName, region));
        return FetchMySqlFlexPricesFn(skuName, region);
    }

    public IReadOnlyList<PriceRow> FetchMySqlStoragePrice(string region)
    {
        FetchMySqlStoragePriceCalls.Add(region);
        return FetchMySqlStoragePriceFn(region);
    }

    public IReadOnlyList<PriceRow> FetchCosmosPrices(string region)
    {
        FetchCosmosPricesCalls.Add(region);
        return FetchCosmosPricesFn(region);
    }

    public IReadOnlyList<PriceRow> FetchRedisPrices(string skuName, string region)
    {
        FetchRedisPricesCalls.Add((skuName, region));
        return FetchRedisPricesFn(skuName, region);
    }

    public IReadOnlyList<PriceRow> FetchAzureManagedRedisPrices(string region)
    {
        FetchAzureManagedRedisPricesCalls.Add(region);
        return FetchAzureManagedRedisPricesFn(region);
    }

    public IReadOnlyList<PriceRow> FetchPublicIpPrices(string region)
    {
        FetchPublicIpPricesCalls.Add(region);
        return FetchPublicIpPricesFn(region);
    }

    public IReadOnlyList<PriceRow> FetchSnapshotPrices(string region)
    {
        FetchSnapshotPricesCalls.Add(region);
        return FetchSnapshotPricesFn(region);
    }
}
