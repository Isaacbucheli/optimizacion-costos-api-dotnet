using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Implementación de <see cref="IPricingConstants"/> que LEE de dbo.pricing_constants
/// (con cache en memoria), port 1:1 de app/pricing/constants.py.
///
/// CRÍTICO (paridad de costos): los valores NO se hardcodean. Se leen de la tabla,
/// igual que el FastAPI; los literales (730, 0.375, 0.10, ...) son solo el FALLBACK
/// por-clave cuando la clave no existe en la tabla — exactamente como get(key, default)
/// del Python. Si un valor se cambia en dbo.pricing_constants, este código lo refleja.
///
/// El cache se carga una vez (como _CONSTANTS_CACHE de Python). Si la lectura de BD
/// falla, NO se silencia: la excepción se propaga (preferible a calcular con números
/// equivocados sin avisar).
/// </summary>
public sealed class PricingConstants(ISqlConnectionFactory factory) : IPricingConstants
{
    private readonly object _lock = new();
    private IReadOnlyDictionary<string, double>? _cache;

    private IReadOnlyDictionary<string, double> LoadConstants()
    {
        if (_cache is not null) return _cache;
        lock (_lock)
        {
            if (_cache is not null) return _cache;

            var dict = new Dictionary<string, double>(StringComparer.Ordinal);
            using var conn = factory.OpenAsync().GetAwaiter().GetResult();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT constant_key, constant_value FROM dbo.pricing_constants";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                dict[reader.GetString(0)] = Convert.ToDouble(reader.GetValue(1));
            }
            _cache = dict;
            return _cache;
        }
    }

    /// <summary>Equivale a constants.get(key, default): valor de BD o el default por-clave.</summary>
    private double Get(string key, double @default)
        => LoadConstants().TryGetValue(key, out var v) ? v : @default;

    /// <summary>Fuerza recarga en la próxima llamada (como constants.reset_cache).</summary>
    public void ResetCache()
    {
        lock (_lock) { _cache = null; }
    }

    public double HoursPerMonth() => Get("hours_per_month", 730.0);

    public double HoursPerYear() => Get("hours_per_year", 8760.0);

    /// <summary>
    /// Tarifa por vCore/hora de la licencia de SQL Server sobre una VM.
    ///
    /// Verificado 2026-08-21 contra la Azure Retail Prices API. Los meters viven en
    /// <c>serviceName eq 'Virtual Machines Licenses'</c> (no bajo "Virtual Machines" ni "SQL Server")
    /// y son INDEPENDIENTES de región: armRegionName llega vacío, así que el precio es el mismo en
    /// cualquier región pública. Mismos valores para Windows y Linux.
    ///   Enterprise  0.375   (meter "1 vCore License", producto "SQL Server Enterprise")
    ///   Standard    0.10    (meter "1 vCore License", producto "SQL Server Standard")
    ///   Web         0.008   (meter "1 vCore License", producto "SQL Server Web")
    ///   Developer / Express: no existe meter por vCore, solo uno plano en 0.
    ///   AHUB (Azure Hybrid Benefit) y DR (Disaster Recovery Benefit): meters en 0, el cliente
    ///   aporta la licencia.
    /// El 0.3753 que estuvo acá hasta el 2026-08-21 no corresponde a ningún meter de Azure.
    /// </summary>
    public double SqlAddonPerVcoreHour(string edition, string licenseType)
    {
        var editionLower = (edition ?? "").ToLowerInvariant();
        var licenseLower = (licenseType ?? "").ToLowerInvariant();

        // El cliente trae la licencia: AHUB (híbrido) y DR (réplica de recuperación) facturan 0.
        if (licenseLower is "ahub" or "dr") return 0.0;
        if (editionLower is "developer" or "express") return 0.0;
        if (editionLower == "enterprise") return Get("sql_enterprise_payg_hr", 0.375);
        if (editionLower == "standard") return Get("sql_standard_payg_hr", 0.10);
        if (editionLower == "web") return Get("sql_web_payg_hr", 0.008);
        // edition desconocida: no aplicar add-on.
        return 0.0;
    }

    public double PublicIpMonthlyCost(string sku)
        => (sku ?? "").ToLowerInvariant() == "standard"
            ? Get("public_ip_standard_month", 3.65)
            : Get("public_ip_basic_month", 2.63);
}
