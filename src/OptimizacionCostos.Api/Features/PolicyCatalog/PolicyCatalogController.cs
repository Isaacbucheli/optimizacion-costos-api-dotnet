using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.PolicyCatalog;

/// <summary>
/// CRUD del catalogo de politicas (linea base BIT de Azure Policy). Espejo del
/// catalogo de alertas: ver -> cualquier autenticado; crear/editar/eliminar -> admin/consultor.
/// </summary>
[ApiController]
[Route("policy-catalog")]
[Authorize]
[RequireModule(Modules.Policies)]
public sealed class PolicyCatalogController(IPolicyCatalogStore store) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<PolicyItem>> ListPolicies(CancellationToken ct)
        => await store.ListPoliciesAsync(ct: ct);

    [HttpGet("{policyId:int}")]
    public async Task<IActionResult> GetPolicy(int policyId, CancellationToken ct)
    {
        var policy = await store.GetPolicyAsync(policyId, ct);
        return policy is null ? NotFound(new { detail = "Policy not found" }) : Ok(policy);
    }

    [HttpPost]
    [RequireModule(Modules.Policies, ModuleAccess.Edit)]
    public async Task<IActionResult> CreatePolicy([FromBody] PolicyCreate payload, CancellationToken ct)
    {
        var id = await store.CreatePolicyAsync(payload, ct);
        return Ok(new { message = "Política creada", policy_id = id });
    }

    [HttpPut("{policyId:int}")]
    [RequireModule(Modules.Policies, ModuleAccess.Edit)]
    public async Task<IActionResult> UpdatePolicy(int policyId, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (fields, error) = BuildFields(body, PolicyColumns.Policy, PolicyColumns.MaxLengths);
        if (error is not null) return BadRequest(new { detail = error });
        if (fields.Count == 0) return BadRequest(new { detail = "No fields to update" });
        if (!await store.UpdatePolicyAsync(policyId, fields, ct))
            return NotFound(new { detail = "Policy not found" });
        return Ok(new { message = "Política actualizada", policy_id = policyId });
    }

    [HttpDelete("{policyId:int}")]
    [RequireModule(Modules.Policies, ModuleAccess.Edit)]
    public async Task<IActionResult> DeletePolicy(int policyId, CancellationToken ct)
    {
        if (!await store.SoftDeletePolicyAsync(policyId, ct))
            return NotFound(new { detail = "Policy not found" });
        return Ok(new { message = "Política desactivada", policy_id = policyId });
    }

    /// <summary>
    /// Construye el diccionario de campos a actualizar tomando solo las claves
    /// presentes en el body que esten en la whitelist (semantica exclude_unset).
    /// Valida `name` si viene presente y que todo texto quepa en su columna.
    /// </summary>
    private static (Dictionary<string, object?> Fields, string? Error) BuildFields(
        JsonElement body, string[] allowed, IReadOnlyDictionary<string, int> maxLengths)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (body.ValueKind != JsonValueKind.Object)
            return (fields, "Invalid body");

        foreach (var col in allowed)
        {
            if (!body.TryGetProperty(col, out var el)) continue;
            var value = PolicyCatalogSchema.JsonToDb(el);

            // name es el unico obligatorio y conserva su mensaje propio (contrato del front): vacio
            // no se acepta ni en un PUT parcial, y el largo sale del mismo mapa que el resto.
            if (col == "name")
            {
                var name = value as string;
                if (string.IsNullOrEmpty(name) ||
                    (maxLengths.TryGetValue(col, out var nameMax) && name.Length > nameMax))
                    return (fields, "Invalid name");
            }
            fields[col] = value;
        }

        // Los largos se revisan al final para que el mensaje nombre el campo culpable en vez de
        // dejar que SQL Server tumbe la conexion con el 8152 (ver ColumnLimits).
        var tooLong = ColumnLimits.FirstViolation(fields, maxLengths);
        if (tooLong is not null) return (fields, tooLong);
        return (fields, null);
    }
}
