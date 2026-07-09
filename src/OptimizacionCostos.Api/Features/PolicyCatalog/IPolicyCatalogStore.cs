namespace OptimizacionCostos.Api.Features.PolicyCatalog;

/// <summary>
/// Repositorio del catalogo de politicas (Azure Policy). Espejo de IAlertCatalogStore.
/// El update recibe solo las columnas presentes (semantica exclude_unset).
/// </summary>
public interface IPolicyCatalogStore
{
    Task<IReadOnlyList<PolicyItem>> ListPoliciesAsync(bool includeInactive = false, CancellationToken ct = default);
    Task<PolicyItem?> GetPolicyAsync(int policyId, CancellationToken ct = default);
    Task<int> CreatePolicyAsync(PolicyCreate data, CancellationToken ct = default);
    Task<bool> UpdatePolicyAsync(int policyId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);
    Task<bool> SoftDeletePolicyAsync(int policyId, CancellationToken ct = default);
}

/// <summary>Columnas mutables permitidas (whitelist anti-inyeccion al construir SET).</summary>
public static class PolicyColumns
{
    public static readonly string[] Policy =
    [
        "policy_number", "name", "category", "policy_type", "recommended_effect",
        "mode", "key_parameters", "description", "objective", "recommended_scope",
        "rollout", "risk", "example_parameters", "azure_cli", "powershell",
        "script_notes", "official_source",
    ];
}
