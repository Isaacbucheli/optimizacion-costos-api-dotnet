using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;

namespace OptimizacionCostos.Api.Features.Consultants;

/// <summary>
/// Asignación de consultores (Gestión CDC): directorio de personas + asignaciones
/// cliente×servicio con varios consultores por rol + reasignación masiva.
/// Ver -> cualquier autenticado; crear/editar/eliminar/reasignar -> SOLO admin
/// (decisión gerencial; a diferencia de los catálogos, consultor NO muta).
/// </summary>
[ApiController]
[Route("consultant-assignments")]
[Authorize]
public sealed class ConsultantsController(IConsultantsStore store) : ControllerBase
{
    // ---- Asignaciones ----

    [HttpGet]
    public async Task<IReadOnlyList<AssignmentItem>> ListAssignments(CancellationToken ct)
        => await store.ListAssignmentsAsync(ct);

    [HttpGet("{assignmentId:int}")]
    public async Task<IActionResult> GetAssignment(int assignmentId, CancellationToken ct)
    {
        var item = await store.GetAssignmentAsync(assignmentId, ct);
        return item is null ? NotFound(new { detail = "Asignación no encontrada" }) : Ok(item);
    }

    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> CreateAssignment([FromBody] AssignmentCreate payload, CancellationToken ct)
    {
        if (payload.PrincipalIds is not { Length: > 0 })
            return BadRequest(new { detail = "Se requiere al menos un consultor principal" });

        var referenced = CollectPersonIds(payload.PrincipalIds, payload.BackupIds,
            payload.CoordinatorId, payload.ComercialId);
        var invalid = await FindInvalidPersonIdsAsync(referenced, ct);
        if (invalid.Count > 0)
            return BadRequest(new { detail = InvalidPeopleDetail(invalid) });

        var id = await store.CreateAssignmentAsync(payload, ct);
        return Ok(new { message = "Asignación creada", assignment_id = id });
    }

    [HttpPut("{assignmentId:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> UpdateAssignment(int assignmentId, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return BadRequest(new { detail = "Invalid body" });

        var (fields, error) = BuildAssignmentFields(body);
        if (error is not null) return BadRequest(new { detail = error });

        var (principalIds, backupIds, setsError) = ParseRoleSets(body);
        if (setsError is not null) return BadRequest(new { detail = setsError });

        if (fields.Count == 0 && principalIds is null && backupIds is null)
            return BadRequest(new { detail = "No fields to update" });

        // Valida personas referenciadas: conjuntos nuevos + coordinator/comercial no nulos.
        var referenced = new List<int>();
        if (principalIds is not null) referenced.AddRange(principalIds);
        if (backupIds is not null) referenced.AddRange(backupIds);
        if (fields.TryGetValue("coordinator_id", out var coord) && coord is int coordId) referenced.Add(coordId);
        if (fields.TryGetValue("comercial_id", out var comercial) && comercial is int comercialId) referenced.Add(comercialId);
        var invalid = await FindInvalidPersonIdsAsync(referenced, ct);
        if (invalid.Count > 0)
            return BadRequest(new { detail = InvalidPeopleDetail(invalid) });

        if (!await store.UpdateAssignmentAsync(assignmentId, fields, principalIds, backupIds, ct))
            return NotFound(new { detail = "Asignación no encontrada" });
        return Ok(new { message = "Asignación actualizada", assignment_id = assignmentId });
    }

    [HttpDelete("{assignmentId:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeleteAssignment(int assignmentId, CancellationToken ct)
    {
        if (!await store.SoftDeleteAssignmentAsync(assignmentId, ct))
            return NotFound(new { detail = "Asignación no encontrada" });
        return Ok(new { message = "Asignación desactivada", assignment_id = assignmentId });
    }

    // ---- Personas ----

    /// <summary>Directorio completo: activas e inactivas, con su flag (el front decide).</summary>
    [HttpGet("people")]
    public async Task<IReadOnlyList<PersonItem>> ListPeople(CancellationToken ct)
        => await store.ListPeopleAsync(ct);

    [HttpPost("people")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> CreatePerson([FromBody] PersonCreate payload, CancellationToken ct)
    {
        payload.PersonType = payload.PersonType.Trim().ToLowerInvariant();
        if (!ConsultantValues.PersonTypes.Contains(payload.PersonType))
            return BadRequest(new { detail = "Tipo de persona inválido" });

        var id = await store.CreatePersonAsync(payload, ct);
        return Ok(new { message = "Persona creada", person_id = id });
    }

    [HttpPut("people/{personId:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> UpdatePerson(int personId, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return BadRequest(new { detail = "Invalid body" });

        var (fields, error) = BuildPersonFields(body);
        if (error is not null) return BadRequest(new { detail = error });
        if (fields.Count == 0) return BadRequest(new { detail = "No fields to update" });

        if (!await store.UpdatePersonAsync(personId, fields, ct))
            return NotFound(new { detail = "Persona no encontrada" });
        return Ok(new { message = "Persona actualizada", person_id = personId });
    }

    /// <summary>Soft-delete: la persona queda inactiva; sus vínculos históricos se conservan.</summary>
    [HttpDelete("people/{personId:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> DeletePerson(int personId, CancellationToken ct)
    {
        if (!await store.SoftDeletePersonAsync(personId, ct))
            return NotFound(new { detail = "Persona no encontrada" });
        return Ok(new { message = "Persona desactivada", person_id = personId });
    }

    // ---- Reasignación masiva ----

    [HttpPost("people/{personId:int}/reassign")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> ReassignPerson(int personId, [FromBody] ReassignRequest payload, CancellationToken ct)
    {
        var toPersonId = payload.ToPersonId!.Value; // [Required] garantiza no-null
        if (toPersonId == personId)
            return BadRequest(new { detail = "La persona origen y destino son la misma" });

        var (scopes, scopeError) = NormalizeScopes(payload.Scopes);
        if (scopeError is not null) return BadRequest(new { detail = scopeError });

        // La origen PUEDE estar inactiva: el flujo operativo real es "la persona sale ->
        // se desactiva -> se reasignan sus clientes". Solo inexistente da 404.
        var origin = await store.GetPersonAsync(personId, ct);
        if (origin is null)
            return NotFound(new { detail = "Persona no encontrada" });

        var target = await store.GetPersonAsync(toPersonId, ct);
        if (target is null || !target.IsActive)
            return BadRequest(new { detail = "Persona destino inválida o inactiva" });

        var changed = await store.ReassignAsync(personId, toPersonId, scopes, ct);
        return Ok(new { message = "Reasignación aplicada", changed_assignments = changed });
    }

    // ---- Helpers ----

    /// <summary>
    /// Escalares presentes en el body que estén en la whitelist (semántica exclude_unset).
    /// coordinator_id/comercial_id aceptan null explícito para limpiar; contract_end debe
    /// ser fecha ISO o null.
    /// </summary>
    private static (Dictionary<string, object?> Fields, string? Error) BuildAssignmentFields(JsonElement body)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var col in ConsultantColumns.Assignment)
        {
            if (!body.TryGetProperty(col, out var el)) continue;
            var value = ConsultantsSchema.JsonToDb(el);

            switch (col)
            {
                case "client_name":
                    if (value is not string clientName || clientName.Length is 0 or > 200)
                        return (fields, "Invalid client_name");
                    break;
                case "coordinator_id" or "comercial_id":
                    if (value is not null and not int)
                        return (fields, $"Invalid {col}");
                    break;
                case "contract_end":
                    if (value is not null)
                    {
                        if (value is not string raw || !DateOnly.TryParseExact(raw, "yyyy-MM-dd", out var date))
                            return (fields, "Invalid contract_end");
                        value = date.ToDateTime(TimeOnly.MinValue);
                    }
                    break;
            }
            fields[col] = value;
        }
        return (fields, null);
    }

    private static (Dictionary<string, object?> Fields, string? Error) BuildPersonFields(JsonElement body)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var col in ConsultantColumns.Person)
        {
            if (!body.TryGetProperty(col, out var el)) continue;
            var value = ConsultantsSchema.JsonToDb(el);

            switch (col)
            {
                case "name":
                    if (value is not string name || name.Length is 0 or > 200)
                        return (fields, "Invalid name");
                    break;
                case "person_type":
                    var type = (value as string)?.Trim().ToLowerInvariant();
                    if (type is null || !ConsultantValues.PersonTypes.Contains(type))
                        return (fields, "Tipo de persona inválido");
                    value = type;
                    break;
                case "is_active":
                    if (value is not bool)
                        return (fields, "Invalid is_active");
                    break;
            }
            fields[col] = value;
        }
        return (fields, null);
    }

    /// <summary>principal_ids/backup_ids si vienen: arrays de int. principal_ids vacío -> error.</summary>
    private static (int[]? Principals, int[]? Backups, string? Error) ParseRoleSets(JsonElement body)
    {
        int[]? principals = null;
        int[]? backups = null;

        if (body.TryGetProperty("principal_ids", out var pEl))
        {
            if (!TryReadIntArray(pEl, out principals))
                return (null, null, "Invalid principal_ids");
            if (principals!.Length == 0)
                return (null, null, "Se requiere al menos un consultor principal");
        }
        if (body.TryGetProperty("backup_ids", out var bEl) && !TryReadIntArray(bEl, out backups))
            return (null, null, "Invalid backup_ids");

        return (principals, backups, null);
    }

    private static bool TryReadIntArray(JsonElement el, out int[]? values)
    {
        values = null;
        if (el.ValueKind != JsonValueKind.Array) return false;

        var list = new List<int>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var n)) return false;
            list.Add(n);
        }
        values = list.ToArray();
        return true;
    }

    /// <summary>Scopes normalizados; null o vacío -> los cuatro (default del spec).</summary>
    private static (IReadOnlyList<string> Scopes, string? Error) NormalizeScopes(string[]? scopes)
    {
        if (scopes is null || scopes.Length == 0)
            return (ConsultantValues.AllScopes, null);

        var result = new List<string>();
        foreach (var raw in scopes)
        {
            var scope = raw?.Trim().ToLowerInvariant();
            if (scope is null || !ConsultantValues.AllScopes.Contains(scope))
                return ([], "Alcance inválido: use principal, backup, coordinador o comercial");
            if (!result.Contains(scope)) result.Add(scope);
        }
        return (result, null);
    }

    private static List<int> CollectPersonIds(int[]? principals, int[]? backups, int? coordinatorId, int? comercialId)
    {
        var ids = new List<int>();
        if (principals is not null) ids.AddRange(principals);
        if (backups is not null) ids.AddRange(backups);
        if (coordinatorId is not null) ids.Add(coordinatorId.Value);
        if (comercialId is not null) ids.Add(comercialId.Value);
        return ids;
    }

    /// <summary>Ids referenciados que NO existen o están inactivos (orden ascendente).</summary>
    private async Task<IReadOnlyList<int>> FindInvalidPersonIdsAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var distinct = ids.Distinct().ToArray();
        var active = await store.GetActivePersonIdsAsync(distinct, ct);
        return distinct.Where(id => !active.Contains(id)).OrderBy(id => id).ToList();
    }

    private static string InvalidPeopleDetail(IReadOnlyList<int> invalid)
        => $"Personas inválidas o inactivas: {string.Join(", ", invalid)}";
}
