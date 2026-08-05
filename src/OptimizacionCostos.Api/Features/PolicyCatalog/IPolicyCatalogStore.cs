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

    /// <summary>
    /// Ancho de las columnas acotadas de dbo.policy_catalog (ver PolicyCatalogSchema). Solo las
    /// NVARCHAR(n): las NVARCHAR(MAX) se omiten a propósito y policy_number es INT. Si cambia el
    /// esquema hay que mover este mapa con él — es lo que evita el 8152 en el UPDATE.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> MaxLengths = new Dictionary<string, int>
    {
        ["name"] = 300,
        ["category"] = 160,
        ["policy_type"] = 80,
        ["recommended_effect"] = 60,
        ["mode"] = 40,
        ["key_parameters"] = 300,
        ["recommended_scope"] = 200,
        ["official_source"] = 500,
    };
}
