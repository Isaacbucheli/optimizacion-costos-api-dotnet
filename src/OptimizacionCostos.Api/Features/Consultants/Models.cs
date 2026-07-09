using System.ComponentModel.DataAnnotations;

namespace OptimizacionCostos.Api.Features.Consultants;

/// <summary>Referencia liviana a una persona, embebida en las asignaciones ({person_id, name}).</summary>
public sealed record PersonRef(int PersonId, string Name);

/// <summary>Persona del directorio BIT (modelo de salida). Las claves JSON salen en snake_case.</summary>
public sealed record PersonItem
{
    public int PersonId { get; init; }
    public string Name { get; init; } = "";
    public string? Email { get; init; }
    public string PersonType { get; init; } = "";
    public bool IsActive { get; init; }
}

/// <summary>
/// Asignación cliente×servicio con las personas embebidas: principals/backups como
/// arrays de {person_id, name} (ordenados por nombre) y coordinator/comercial como
/// objeto o null.
/// </summary>
public sealed record AssignmentItem
{
    public int AssignmentId { get; init; }
    public string ClientName { get; init; } = "";
    public string? Service { get; init; }
    public string? Category { get; init; }
    public string? Databases { get; init; }
    public string? Country { get; init; }
    public string? Status { get; init; }
    public string? AccessAccounts { get; init; }
    public string? AccountRole { get; init; }
    public string? Lighthouse { get; init; }
    public string? ClientContactName { get; init; }
    public string? ClientContactPhone { get; init; }
    public string? ClientContactEmail { get; init; }
    public DateOnly? ContractEnd { get; init; }
    public string? Observations { get; init; }
    public bool IsActive { get; init; }
    public PersonRef? Coordinator { get; init; }
    public PersonRef? Comercial { get; init; }
    public IReadOnlyList<PersonRef> Principals { get; init; } = [];
    public IReadOnlyList<PersonRef> Backups { get; init; } = [];
}

// ---- DTOs de entrada ----

public sealed class PersonCreate
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = "";

    public string? Email { get; set; }

    [Required]
    public string PersonType { get; set; } = "";
}

public sealed class AssignmentCreate
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string ClientName { get; set; } = "";

    public string? Service { get; set; }
    public string? Category { get; set; }
    public string? Databases { get; set; }
    public string? Country { get; set; }
    public string? Status { get; set; }
    public string? AccessAccounts { get; set; }
    public string? AccountRole { get; set; }
    public string? Lighthouse { get; set; }
    public string? ClientContactName { get; set; }
    public string? ClientContactPhone { get; set; }
    public string? ClientContactEmail { get; set; }
    public DateOnly? ContractEnd { get; set; }
    public string? Observations { get; set; }
    public int? CoordinatorId { get; set; }
    public int? ComercialId { get; set; }
    public int[]? PrincipalIds { get; set; }
    public int[]? BackupIds { get; set; }
}

/// <summary>Body de la reasignación masiva: persona destino + alcance (default los cuatro).</summary>
public sealed class ReassignRequest
{
    [Required]
    public int? ToPersonId { get; set; }

    public string[]? Scopes { get; set; }
}

/// <summary>Valores válidos del módulo (tipos de persona y alcances de reasignación).</summary>
public static class ConsultantValues
{
    public static readonly IReadOnlyList<string> AllScopes =
        ["principal", "backup", "coordinador", "comercial"];

    public static readonly HashSet<string> PersonTypes = new(StringComparer.Ordinal)
    {
        "consultor", "coordinador", "comercial",
    };
}
