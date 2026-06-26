namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// PLACEHOLDERS de Fase 1. Cada método get_* de <see cref="IPriceRepository"/> está aquí
/// como stub que lanza <see cref="NotImplementedException"/> para que el proyecto API
/// COMPILE mientras los agentes por servicio aún no portan su lógica.
///
/// CÓMO TRABAJAR (agente por servicio):
///   1. BORRA de este archivo el/los método(s) de tu servicio.
///   2. Impleméntalo(s) en un archivo parcial propio
///      (ej. SqlPriceRepository.Vm.cs, SqlPriceRepository.AppService.cs) con el mismo
///      cuerpo que la función get_* equivalente de price_repository.py, usando
///      _cache / _client / _constants, los selectores de <see cref="PriceSelectors"/>
///      y, si hace falta, RefreshIfStale / RefreshByFetchQueryIfStale.
///
/// Cuando los 12 métodos hayan migrado, este archivo quedará vacío y se puede eliminar.
/// NO agregues lógica aquí: solo es el andamio temporal.
///
/// Mapeo método → función Python / servicio (clave del CalculatorRegistry):
///   GetVmPrices                 → get_vm_prices                     (vm)
///   GetDiskPrice                → get_disk_price                    (disk)
///   GetPublicIpMonthlyPrice     → get_public_ip_monthly_price       (public_ip)
///   GetElasticPremiumBasePrice  → get_elastic_premium_base_price    (app_service: EP*/WS*)
///   GetAppServicePrices         → get_app_service_prices            (app_service)
///   GetMySqlFlexPrices          → get_mysql_flex_prices             (mysql)
///   GetMySqlStoragePricePerGb   → get_mysql_storage_price_per_gb    (mysql)
///   GetRedisPrices              → get_redis_prices                  (redis)
///   GetSqlDbPriceDetails        → get_sql_db_price_details          (sql_db)
///   GetSqlManagedInstancePrices → get_sql_managed_instance_prices   (sql_mi)
///   GetSynapseDwPrices          → get_synapse_dw_prices             (synapse_dw)
///   GetElasticPoolPrices        → get_elastic_pool_prices           (elastic_pool)
///   (cosmos no expone get_*: la calculadora Cosmos no consulta precios via este repo)
/// </summary>
public sealed partial class SqlPriceRepository
{
    private const string NotPorted =
        "Fase 1: método de precio aún no portado desde price_repository.py.";

}
