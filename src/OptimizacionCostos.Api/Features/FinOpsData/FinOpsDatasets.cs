namespace OptimizacionCostos.Api.Features.FinOpsData;

/// <summary>
/// Datasets open data del Microsoft FinOps Toolkit (MIT) — github.com/microsoft/finops-toolkit.
/// URL estable releases/latest/download/&lt;archivo&gt;. StaleDays: eligibility se actualiza
/// semanalmente (GitHub Actions); el resto con cada release (~mensual).
/// </summary>
public sealed record FinOpsDataset(string Key, string FileName, string[] ExpectedHeaderStart, int MinRows, int StaleDays);

public static class FinOpsDatasets
{
    public const string BaseUrl = "https://github.com/microsoft/finops-toolkit/releases/latest/download/";

    public static readonly FinOpsDataset PricingUnits = new(
        "pricing_units", "PricingUnits.csv", ["UnitOfMeasure", "AccountTypes"], 300, 30);
    public static readonly FinOpsDataset Regions = new(
        "regions", "Regions.csv", ["OriginalValue", "RegionId", "RegionName"], 400, 30);
    public static readonly FinOpsDataset Services = new(
        "services", "Services.csv", ["ConsumedService", "ResourceType", "ServiceName", "ServiceCategory"], 300, 30);
    public static readonly FinOpsDataset ResourceTypes = new(
        "resource_types", "ResourceTypes.csv", ["ResourceType", "SingularDisplayName"], 1500, 30);
    public static readonly FinOpsDataset CommitmentEligibility = new(
        "commitment_eligibility", "CommitmentDiscountEligibility.csv",
        ["MeterId", "x_CommitmentDiscountSpendEligibility", "x_CommitmentDiscountUsageEligibility"], 100_000, 7);

    public static readonly IReadOnlyList<FinOpsDataset> All =
        [PricingUnits, Regions, Services, ResourceTypes, CommitmentEligibility];
}
