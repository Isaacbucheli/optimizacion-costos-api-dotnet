namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Cliente de la API de Azure Retail Prices (https://prices.azure.com/api/retail/prices).
/// Port de app/pricing/azure_retail_client.py: un método <c>FetchX</c> por cada
/// <c>fetch_*</c> de Python. Cada uno arma filtros OData, pagina por NextPageLink (hasta
/// MAX_PAGES = 50) con currencyCode = "USD", y devuelve las filas ya mapeadas a
/// <see cref="PriceRow"/> (incluyendo el filtrado post-fetch por productName que hacen
/// algunos fetch_* en Python).
///
/// La implementación HTTP real (HttpClient) es de otra fase; aquí solo el contrato que
/// invocan los métodos get_* de <see cref="SqlPriceRepository"/>. La paridad de firmas
/// con Python es exacta para que el porte por servicio sea mecánico.
/// </summary>
public interface IRetailPriceClient
{
    /// <summary>
    /// Python: <c>fetch_vm_prices(arm_sku_names, region, os_type=None)</c>. Consumption + Reservation
    /// 1Y/3Y para los SKUs dados. Filtro extra por OS (Windows/Linux) si <paramref name="osType"/> no es null.
    /// </summary>
    IReadOnlyList<PriceRow> FetchVmPrices(IReadOnlyList<string> armSkuNames, string region, string? osType = null);

    /// <summary>Python: <c>fetch_vm_region_prices(region)</c>. Precios amplios de VMs por región (fallback).</summary>
    IReadOnlyList<PriceRow> FetchVmRegionPrices(string region);

    /// <summary>Python: <c>fetch_managed_disk_prices(sku_name, region)</c>. Solo "Managed Disks".</summary>
    IReadOnlyList<PriceRow> FetchManagedDiskPrices(string skuName, string region);

    /// <summary>
    /// Python: <c>fetch_sql_db_prices(region, sku_name=None)</c>. SQL Database (DTU + vCore).
    /// Si <paramref name="skuName"/> es null/Basic/GP_S_Gen5 no se filtra por skuName en el OData.
    /// </summary>
    IReadOnlyList<PriceRow> FetchSqlDbPrices(string region, string? skuName = null);

    /// <summary>Python: <c>fetch_elastic_pool_prices(region)</c>. Solo productos con "Elastic Pool".</summary>
    IReadOnlyList<PriceRow> FetchElasticPoolPrices(string region);

    /// <summary>
    /// Python: <c>fetch_synapse_dw_prices(region)</c>. Solo "Dedicated SQL Pool" y "SQL Provisioned DWU".
    /// </summary>
    IReadOnlyList<PriceRow> FetchSynapseDwPrices(string region);

    /// <summary>Python: <c>fetch_sql_managed_instance_prices(region)</c>. Compute + storage + reservas.</summary>
    IReadOnlyList<PriceRow> FetchSqlManagedInstancePrices(string region);

    /// <summary>
    /// Python: <c>fetch_app_service_prices(sku_name, region, is_linux=False)</c>. Genera candidatos de
    /// sku (D1→Shared, "P2v3"→"P2 v3"), normaliza el skuName devuelto al original, y filtra Linux/Windows.
    /// </summary>
    IReadOnlyList<PriceRow> FetchAppServicePrices(string skuName, string region, bool isLinux = false);

    /// <summary>Python: <c>fetch_app_service_region_prices(region)</c>. Precios amplios por región (fallback).</summary>
    IReadOnlyList<PriceRow> FetchAppServiceRegionPrices(string region);

    /// <summary>Python: <c>fetch_functions_premium_prices(region)</c>. serviceName "Functions" (planes EP*).</summary>
    IReadOnlyList<PriceRow> FetchFunctionsPremiumPrices(string region);

    /// <summary>Python: <c>fetch_logic_apps_standard_prices(region)</c>. serviceName "Logic Apps" (planes WS*).</summary>
    IReadOnlyList<PriceRow> FetchLogicAppsStandardPrices(string region);

    /// <summary>
    /// Python: <c>fetch_mysql_flex_prices(sku_name, region)</c>. Solo "Flexible Server"; agrega
    /// armSkuName al filtro si el sku empieza por "Standard_B".
    /// </summary>
    IReadOnlyList<PriceRow> FetchMySqlFlexPrices(string skuName, string region);

    /// <summary>Python: <c>fetch_mysql_storage_price(region)</c>. "Flexible Server" + meter con "Storage".</summary>
    IReadOnlyList<PriceRow> FetchMySqlStoragePrice(string region);

    /// <summary>Python: <c>fetch_cosmos_prices(region)</c>. Azure Cosmos DB (RU/s, autoscale, serverless).</summary>
    IReadOnlyList<PriceRow> FetchCosmosPrices(string region);

    /// <summary>Python: <c>fetch_redis_prices(sku_name, region)</c>. serviceName "Redis Cache" (clásico).</summary>
    IReadOnlyList<PriceRow> FetchRedisPrices(string skuName, string region);

    /// <summary>
    /// Python: <c>fetch_azure_managed_redis_prices(region)</c>. serviceName "Azure Cache for Redis";
    /// filtra a productos "Enterprise" / "Managed Redis".
    /// </summary>
    IReadOnlyList<PriceRow> FetchAzureManagedRedisPrices(string region);

    /// <summary>Python: <c>fetch_public_ip_prices(region)</c>. serviceName "Virtual Network", productName "IP Addresses".</summary>
    IReadOnlyList<PriceRow> FetchPublicIpPrices(string region);

    /// <summary>
    /// Precios de snapshots de managed disk por región (serviceName "Storage", productos
    /// "* Managed Disks", meter "* Snapshots"). Fuente: Microsoft FinOps Toolkit (MIT) — usa la
    /// misma API que el toolkit para right-sizing/limpieza. Net-new (Fase 2).
    /// </summary>
    IReadOnlyList<PriceRow> FetchSnapshotPrices(string region);
}
