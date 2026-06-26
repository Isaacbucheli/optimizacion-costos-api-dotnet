using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>
/// Doble de prueba de <see cref="IPricingConstants"/>. Valores por defecto iguales a
/// app/pricing/constants.py (730 / 8760 / IP 3.65 / 2.63). El add-on SQL se delega a un
/// <c>Func</c> con el port exacto por defecto, y todo es sobre-escribible para tests.
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

    /// <summary>Por defecto, port exacto de sql_addon_per_vcore_hour de constants.py.</summary>
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
        if (licenseLower == "ahub") return 0.0;
        if (editionLower is "developer" or "express") return 0.0;
        if (editionLower == "enterprise") return 0.3753;
        if (editionLower == "standard") return 0.10;
        return 0.0;
    }

    private static double DefaultPublicIp(string sku)
        => (sku ?? "").ToLowerInvariant() == "standard" ? 3.65 : 2.63;
}
