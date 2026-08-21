namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Constantes de precio (horas/mes, add-ons SQL, IP pública). Port de
/// app/pricing/constants.py. Las calculadoras solo usan estos métodos; la fuente
/// de los valores (defaults vs BD) la decide la implementación.
/// </summary>
public interface IPricingConstants
{
    /// <summary>Horas facturables al mes. Python: <c>hours_per_month()</c> (default 730).</summary>
    double HoursPerMonth();

    /// <summary>Horas al año. Python: <c>hours_per_year()</c> (default 8760).</summary>
    double HoursPerYear();

    /// <summary>
    /// Add-on de SQL por vCore por hora según edition + licenseType.
    /// Python: <c>sql_addon_per_vcore_hour(edition, license_type)</c>.
    ///   - AHUB (cualquier edition): 0 (BYOL)
    ///   - Developer / Express: 0
    ///   - Enterprise: 0.375 (PAYG, meter "1 vCore License" de la Retail API)
    ///   - Standard: 0.10 (PAYG)
    ///   - edition desconocida: 0
    /// </summary>
    double SqlAddonPerVcoreHour(string edition, string licenseType);

    /// <summary>
    /// Costo mensual fijo de IP pública según SKU. Python: <c>public_ip_monthly_cost(sku)</c>.
    ///   - "standard": 3.65
    ///   - otro/basic: 2.63
    /// </summary>
    double PublicIpMonthlyCost(string sku);
}
