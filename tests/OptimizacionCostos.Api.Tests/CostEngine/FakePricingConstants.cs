using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Doble de prueba de <see cref="IPricingConstants"/>. Valores por defecto 730 / 8760 / IP 3.65 /
/// 2.63. El add-on SQL se delega a un <c>Func</c> que por defecto espeja a
/// <see cref="PricingConstants.SqlAddonPerVcoreHour"/>, y todo es sobre-escribible para tests.
///
/// El add-on ya NO es el port de app/pricing/constants.py: las tarifas se verificaron el 2026-08-21
/// contra la Azure Retail Prices API (Enterprise 0.375 y no 0.3753, Web 0.008 que faltaba, licencia
/// DR en 0). Este doble sigue a la implementación real para que los tests no se calibren contra
/// tarifas que Azure no cobra.
///
/// Los tests Python parchean <c>constants.hours_per_month</c> a 730 explícitamente; aquí
/// 730 ya es el default, así que basta con <c>new FakePricingConstants()</c>. Para variar:
/// <code>
///   var consts = new FakePricingConstants { HoursPerMonthValue = 720 };
///   consts.SqlAddonPerVcoreHourFn = (_, _) => 0.0;
/// </code>
/// </summary>
public sealed class FakePricingConstants : IPricingConstants
{
    public double HoursPerMonthValue { get; set; } = 730.0;
    public double HoursPerYearValue { get; set; } = 8760.0;

    /// <summary>Por defecto, espejo de PricingConstants.SqlAddonPerVcoreHour.</summary>
    public Func<string, string, double> SqlAddonPerVcoreHourFn { get; set; } = DefaultSqlAddon;

    /// <summary>Por defecto, port exacto de public_ip_monthly_cost de constants.py.</summary>
    public Func<string, double> PublicIpMonthlyCostFn { get; set; } = DefaultPublicIp;

    public double HoursPerMonth() => HoursPerMonthValue;

    public double HoursPerYear() => HoursPerYearValue;

    public double SqlAddonPerVcoreHour(string edition, string licenseType)
        => SqlAddonPerVcoreHourFn(edition, licenseType);

    public double PublicIpMonthlyCost(string sku) => PublicIpMonthlyCostFn(sku);

    private static double DefaultSqlAddon(string edition, string licenseType)
    {
        var editionLower = (edition ?? "").ToLowerInvariant();
        var licenseLower = (licenseType ?? "").ToLowerInvariant();
        if (licenseLower is "ahub" or "dr") return 0.0;
        if (editionLower is "developer" or "express") return 0.0;
        if (editionLower == "enterprise") return 0.375;
        if (editionLower == "standard") return 0.10;
        if (editionLower == "web") return 0.008;
        return 0.0;
    }

    private static double DefaultPublicIp(string sku)
        => (sku ?? "").ToLowerInvariant() == "standard" ? 3.65 : 2.63;
}
