using System.Security.Cryptography;
using System.Text;

namespace OptimizacionCostos.Api.Features.Optimization;

/// <summary>Hallazgo del barrido de optimización. Port de optimization/base.py::Finding.</summary>
public sealed record Finding(
    string CheckId, string Category, string Severity, string SubscriptionId,
    string AzureResourceId, string ResourceName, string ResourceType, string Region,
    IReadOnlyDictionary<string, object?> Details, double? EstimatedMonthlySavings, string Currency = "USD")
{
    /// <summary>SHA256 de "{clientId}|{checkId}|{azureResourceId.lower()}" → 32 bytes.</summary>
    public byte[] Fingerprint(int clientId)
    {
        var raw = $"{clientId}|{CheckId}|{(AzureResourceId ?? "").ToLowerInvariant()}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }
}

/// <summary>Definición de un check (metadata + KQL + función que arma hallazgos).</summary>
public sealed record CheckDefinition(
    string CheckId, string Name, string Description, string Category, int WafPillar, string Severity,
    string ArgQuery, Func<CheckDefinition, IReadOnlyList<OptimizacionCostos.Api.Features.Inventory.RgRow>, string, ICostEstimation, List<Finding>> BuildFindings);

public static class OptCategory { public const string CostWaste = "cost_waste"; public const string Governance = "governance"; }
public static class OptSeverity { public const string High = "high"; public const string Medium = "medium"; public const string Low = "low"; }
