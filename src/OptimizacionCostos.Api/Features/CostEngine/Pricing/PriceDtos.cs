namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// DTOs de retorno del repositorio de precios (port de los dicts que devuelven las
/// funciones <c>prices.X(...)</c> de app/pricing/price_repository.py).
///
/// Regla general: los precios que pueden faltar van como <c>double?</c> (== None en
/// Python). El mapeo dict→record está documentado en cada record con la clave Python
/// exacta entre paréntesis. Los DTOs son inmutables (records) para evitar mutaciones
/// accidentales en las calculadoras.
/// </summary>

/// <summary>
/// Retorno de <c>get_vm_prices(arm_sku_name, region, os_type)</c> y del selector
/// <c>_select_vm_prices</c>. Dict Python:
/// { "payg_hourly", "ri_1y_total", "ri_3y_total", "os_used", "match_strategy" }.
/// RI es OS-agnostic (precio base Linux). <c>OsUsed</c> indica "Windows" / "Linux" /
/// "Linux (fallback)". <c>MatchStrategy</c> = "deterministic" o estrategia del asistente IA.
/// </summary>
public sealed record VmPrices(
    double? PaygHourly,
    double? Ri1yTotal,
    double? Ri3yTotal,
    string? OsUsed = null,
    string? MatchStrategy = null,
    string? PaygMeterId = null);

/// <summary>
/// Retorno de <c>get_app_service_prices(arm_sku_name, region, is_linux)</c> y del
/// selector <c>_select_app_service_prices</c>. Dict Python:
/// { "payg_hourly", "ri_1y_total", "ri_3y_total", "match_strategy" }.
/// </summary>
public sealed record AppServicePrices(
    double? PaygHourly,
    double? Ri1yTotal,
    double? Ri3yTotal,
    string? MatchStrategy = null,
    string? PaygMeterId = null);

/// <summary>
/// Retorno de <c>get_elastic_premium_base_price(sku_name, region)</c> (planes EP*/WS*).
/// Dict Python: { "base_hourly", "vcpu_hourly", "mem_gib_hourly", "vcpu", "gb" }.
/// Devuelve null (None) si no hay spec del SKU o faltan precios de duración.
/// <c>Vcpu</c> es conteo de vCPU (int) y <c>Gb</c> los GiB de RAM del SKU (double).
/// </summary>
public sealed record ElasticPremiumBase(
    double BaseHourly,
    double VcpuHourly,
    double MemGibHourly,
    int Vcpu,
    double Gb);

/// <summary>
/// Retorno de <c>get_sql_db_price_details(sku_name, region, sku_tier, compute_tier)</c>.
/// Dict Python: { "retail_price", "unit_of_measure", "meter_name", "product_name", "sku_name" }.
/// La función devuelve None si no hay item; en C# devolver null. La calculadora trata
/// <c>RetailPrice == null</c> como "price_not_found".
/// </summary>
public sealed record SqlDbPriceDetails(
    double? RetailPrice,
    string? UnitOfMeasure,
    string? MeterName,
    string? ProductName,
    string? SkuName,
    string? MatchStrategy = null,
    string? PaygMeterId = null);

/// <summary>
/// Retorno de <c>get_sql_managed_instance_prices(region, sku_tier, sku_family, vcores, zone_redundant)</c>
/// y del selector <c>_select_sql_mi_prices</c>. Dict Python:
/// { "payg_hourly_per_vcore", "storage_monthly_per_gb", "ri_1y_total_per_vcore",
///   "ri_3y_total_per_vcore", "compute_meter_name", "compute_sku_name",
///   "storage_meter_name", "storage_sku_name", "product_name", "match_strategy", "match_confidence" }.
/// Los precios son POR vCORE (la calculadora multiplica por vcores).
/// </summary>
public sealed record SqlManagedInstancePrices(
    double? PaygHourlyPerVcore,
    double? StorageMonthlyPerGb,
    double? Ri1yTotalPerVcore,
    double? Ri3yTotalPerVcore,
    string? ComputeMeterName = null,
    string? ComputeSkuName = null,
    string? StorageMeterName = null,
    string? StorageSkuName = null,
    string? ProductName = null,
    string? MatchStrategy = null,
    double? MatchConfidence = null,
    string? PaygMeterId = null);

/// <summary>
/// Retorno de <c>get_mysql_flex_prices(arm_sku_name, region, sku_tier)</c> y del
/// selector <c>_select_mysql_flex_prices</c>. Dict Python:
/// { "payg_hourly", "ri_1y_total", "ri_3y_total", "is_per_vcore", "match_strategy", "match_confidence" }.
/// <c>IsPerVcore</c>: si el meter es por vCore, la calculadora multiplica compute por vCores
/// derivados del SKU; si no, usa vcores=1.
/// </summary>
public sealed record MySqlFlexPrices(
    double? PaygHourly,
    double? Ri1yTotal,
    double? Ri3yTotal,
    bool IsPerVcore = false,
    string? MatchStrategy = null,
    double? MatchConfidence = null,
    string? PaygMeterId = null);

/// <summary>
/// Retorno de <c>get_redis_prices(sku_tier, sku_capacity, region)</c> y del selector
/// <c>_select_redis_prices</c>. Dict Python:
/// { "payg_hourly", "ri_1y_total", "ri_3y_total", "match_strategy", "match_confidence" }.
/// </summary>
public sealed record RedisPrices(
    double? PaygHourly,
    double? Ri1yTotal,
    double? Ri3yTotal,
    string? MatchStrategy = null,
    double? MatchConfidence = null,
    string? PaygMeterId = null);

/// <summary>
/// Retorno de <c>get_synapse_dw_prices(dw_level, region)</c> y del selector
/// <c>_select_synapse_dw_prices</c>. Dict Python:
/// { "payg_hourly", "ri_1y_total", "ri_3y_total", "dwu", "blocks" }.
/// RI ya viene escalada por bloques de 100 DWU. <c>Dwu</c> es el total de DWUs y
/// <c>Blocks</c> = max(1, dwu/100).
/// </summary>
public sealed record SynapseDwPrices(
    double? PaygHourly,
    double? Ri1yTotal,
    double? Ri3yTotal,
    int Dwu = 0,
    int Blocks = 1,
    string? PaygMeterId = null);

/// <summary>
/// Retorno de <c>get_elastic_pool_prices(sku_tier, sku_family, capacity, region)</c> y del
/// selector <c>_select_elastic_pool_price</c>. Dict Python:
/// { "payg_hourly", "model", "meter_name", "matched_sku" }.
/// <c>Model</c> = "DTU" o "vCore". <c>PaygHourly</c> ya está normalizado a $/hora
/// (DTU: precio_por_dia / 24). La función devuelve None si no hay match EXACTO; en C#
/// devolver null (NO aproximar — la calculadora marca "price_not_found").
/// </summary>
public sealed record ElasticPoolPrices(
    double? PaygHourly,
    string? Model = null,
    string? MeterName = null,
    string? MatchedSku = null,
    string? PaygMeterId = null);
