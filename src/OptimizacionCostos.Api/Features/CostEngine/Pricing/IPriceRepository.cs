namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Repositorio de precios de Azure Retail. Port de las funciones públicas
/// <c>prices.X(...)</c> de app/pricing/price_repository.py que invocan las calculadoras.
///
/// Cada método consulta Azure Retail Prices API (con cache en SQL) y devuelve precios
/// PAYG/RI ya seleccionados/normalizados. Si no encuentra precio devuelve null o un DTO
/// con sus precios en null (== None en Python). La implementación real vendrá en otra
/// fase; aquí solo el contrato para que las calculadoras y sus tests compilen.
///
/// Nota: la firma usa <c>region</c> ya "tal cual viene"; las calculadoras Python
/// normalizan la región (lower + sin espacios) ANTES de llamar a estos métodos
/// (salvo get_public_ip_monthly_price, que normaliza internamente). Mantener esa
/// convención al portar las calculadoras.
/// </summary>
public interface IPriceRepository
{
    /// <summary>
    /// Precios de Virtual Machine. Python: <c>get_vm_prices(arm_sku_name, region, os_type)</c>.
    /// <paramref name="osType"/> = "Windows" / "Linux". RI es OS-agnostic (base Linux).
    /// </summary>
    VmPrices GetVmPrices(string armSkuName, string region, string osType);

    /// <summary>
    /// Precio mensual de Managed Disk. Python: <c>get_disk_price(sku_tier, region)</c>.
    /// <paramref name="skuTier"/> ej. "E10 LRS", "P10 LRS", "S10 LRS". Null si no hay
    /// meter exacto "&lt;tier&gt; Disk".
    /// </summary>
    double? GetDiskPrice(string skuTier, string region);

    /// <summary>
    /// Precio mensual de Public IP. Python: <c>get_public_ip_monthly_price(sku_name, region, allocation_method)</c>.
    /// OJO: este método normaliza la región internamente (como en Python). Null si no hay match.
    /// </summary>
    double? GetPublicIpMonthlyPrice(string skuName, string region, string allocationMethod = "Static");

    /// <summary>
    /// Costo base por hora de un plan Elastic Premium (EP*) o Workflow Standard (WS*).
    /// Python: <c>get_elastic_premium_base_price(sku_name, region)</c>. Devuelve null si
    /// no hay spec del SKU o faltan precios de duración (vCPU/RAM).
    /// </summary>
    ElasticPremiumBase? GetElasticPremiumBasePrice(string skuName, string region);

    /// <summary>
    /// Precios de App Service Plan. Python: <c>get_app_service_prices(arm_sku_name, region, is_linux)</c>.
    /// </summary>
    AppServicePrices GetAppServicePrices(string armSkuName, string region, bool isLinux);

    /// <summary>
    /// Precios de MySQL Flexible Server (compute). Python: <c>get_mysql_flex_prices(arm_sku_name, region, sku_tier)</c>.
    /// </summary>
    MySqlFlexPrices GetMySqlFlexPrices(string armSkuName, string region, string skuTier = "");

    /// <summary>
    /// Precio de storage por GB/mes de MySQL Flexible Server. Python: <c>get_mysql_storage_price_per_gb(region)</c>.
    /// Null si no hay precio.
    /// </summary>
    double? GetMySqlStoragePricePerGb(string region);

    /// <summary>
    /// Precios de Azure Cache for Redis. Python: <c>get_redis_prices(sku_tier, sku_capacity, region)</c>.
    /// </summary>
    RedisPrices GetRedisPrices(string skuTier, int skuCapacity, string region);

    /// <summary>
    /// Detalle de precio de Azure SQL Database. Python: <c>get_sql_db_price_details(sku_name, region, sku_tier, compute_tier)</c>.
    /// Devuelve null si no hay item (== None). La calculadora también trata RetailPrice null como "price_not_found".
    /// </summary>
    SqlDbPriceDetails? GetSqlDbPriceDetails(string skuName, string region, string skuTier = "", string computeTier = "");

    /// <summary>
    /// Precios de SQL Managed Instance (por vCore). Python:
    /// <c>get_sql_managed_instance_prices(region, sku_tier, sku_family, vcores, zone_redundant)</c>.
    /// </summary>
    SqlManagedInstancePrices GetSqlManagedInstancePrices(
        string region, string skuTier = "", string skuFamily = "", int vcores = 0, bool zoneRedundant = false);

    /// <summary>
    /// Precios de Synapse Dedicated SQL Pool por nivel DWc. Python: <c>get_synapse_dw_prices(dw_level, region)</c>.
    /// <paramref name="dwLevel"/> ej. "DW100c", "DW500c". RI ya escalada por bloques.
    /// </summary>
    SynapseDwPrices GetSynapseDwPrices(string dwLevel, string region);

    /// <summary>
    /// Precio PAYG de un SQL Elastic Pool. Python: <c>get_elastic_pool_prices(sku_tier, sku_family, capacity, region)</c>.
    /// Devuelve null si no hay match exacto verificado (NO aproxima).
    /// </summary>
    ElasticPoolPrices? GetElasticPoolPrices(string skuTier, string skuFamily, int capacity, string region);
}
