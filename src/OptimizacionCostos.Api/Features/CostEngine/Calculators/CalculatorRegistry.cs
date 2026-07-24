using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// Dispatcher de calculadoras. Cada calculator_key del service_catalog mapea
/// a una clase concreta de calculadora.
///
/// Port de app/calculators/__init__.py (diccionario COST_CALCULATORS +
/// función get_calculator). A diferencia del Python, las calculadoras en C#
/// reciben sus dependencias (IPriceRepository e IPricingConstants) por
/// constructor, así que el registry expone una factory que las instancia.
/// </summary>
public static class CalculatorRegistry
{
    /// <summary>
    /// Mapa calculator_key → factory que construye la calculadora con sus
    /// dependencias. Mismas claves y mismo orden que COST_CALCULATORS en Python.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Func<IPriceRepository, IPricingConstants, ICostCalculator>> Factories =
        new Dictionary<string, Func<IPriceRepository, IPricingConstants, ICostCalculator>>(StringComparer.Ordinal)
        {
            ["synapse_dedicated_pool"] = (prices, constants) => new SynapseDedicatedPoolCalculator(prices, constants),
            ["elastic_pool"] = (prices, constants) => new ElasticPoolCalculator(prices, constants),
            ["compute_vm"] = (prices, constants) => new ComputeVmCalculator(prices, constants),
            ["managed_disk"] = (prices, constants) => new ManagedDiskCalculator(prices, constants),
            ["sql_database"] = (prices, constants) => new SqlDatabaseCalculator(prices, constants),
            ["sql_managed_instance"] = (prices, constants) => new SqlManagedInstanceCalculator(prices, constants),
            ["app_service_plan"] = (prices, constants) => new AppServicePlanCalculator(prices, constants),
            ["mysql_flex"] = (prices, constants) => new MySqlFlexCalculator(prices, constants),
            ["cosmos_account"] = (prices, constants) => new CosmosAccountCalculator(prices, constants),
            ["redis_cache"] = (prices, constants) => new RedisCacheCalculator(prices, constants),
            ["public_ip"] = (prices, constants) => new PublicIpCalculator(prices, constants),
            ["sql_vm_metadata"] = (prices, constants) => new SqlVmMetadataCalculator(prices, constants),
            ["snapshot"] = (prices, constants) => new SnapshotCalculator(prices, constants),
        };

    /// <summary>
    /// Claves de calculadora soportadas (para diagnóstico / validación del catálogo).
    /// </summary>
    public static IReadOnlyCollection<string> SupportedKeys => (IReadOnlyCollection<string>)Factories.Keys;

    /// <summary>
    /// Instancia la calculadora asociada a <paramref name="calculatorKey"/>, o devuelve
    /// <c>null</c> si la clave no existe (equivalente a get_calculator devolviendo None).
    /// </summary>
    /// <param name="calculatorKey">Clave del service_catalog (ej. "compute_vm").</param>
    /// <param name="prices">Repositorio de precios que la calculadora consultará.</param>
    /// <param name="constants">Constantes de pricing (regiones, factores, etc.).</param>
    public static ICostCalculator? GetCalculator(string calculatorKey, IPriceRepository prices, IPricingConstants constants)
    {
        if (calculatorKey is null)
        {
            return null;
        }

        return Factories.TryGetValue(calculatorKey, out var factory)
            ? factory(prices, constants)
            : null;
    }
}
