namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Implementación de <see cref="IPricingConstants"/> con los valores por defecto
/// de app/pricing/constants.py. Esta versión NO consulta SQL: devuelve siempre los
/// defaults (730, 8760, 0.3753, 0.10, 3.65, 2.63). La versión que lee la tabla
/// dbo.pricing_constants (con cache) vendrá en una fase posterior; el contrato
/// (IPricingConstants) ya está fijo, así que esa versión solo cambiará los valores
/// de origen sin tocar a las calculadoras.
/// </summary>
public sealed class PricingConstants : IPricingConstants
{
    public double HoursPerMonth() => 730.0;

    public double HoursPerYear() => 8760.0;

    public double SqlAddonPerVcoreHour(string edition, string licenseType)
    {
        var editionLower = (edition ?? "").ToLowerInvariant();
        var licenseLower = (licenseType ?? "").ToLowerInvariant();

        if (licenseLower == "ahub") return 0.0;
        if (editionLower is "developer" or "express") return 0.0;
        if (editionLower == "enterprise") return 0.3753;
        if (editionLower == "standard") return 0.10;
        // edition desconocida: no aplicar add-on.
        return 0.0;
    }

    public double PublicIpMonthlyCost(string sku)
    {
        if ((sku ?? "").ToLowerInvariant() == "standard") return 3.65;
        return 2.63;
    }
}
