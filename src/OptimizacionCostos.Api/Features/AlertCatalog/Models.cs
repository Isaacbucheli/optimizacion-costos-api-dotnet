using System.ComponentModel.DataAnnotations;

namespace OptimizacionCostos.Api.Features.AlertCatalog;

/// <summary>Alerta del catalogo (modelo de salida). Las claves JSON salen en snake_case.</summary>
public sealed record AlertItem
{
    public int AlertId { get; init; }
    public int? AlertNumber { get; init; }
    public string Name { get; init; } = "";
    public string? Resource { get; init; }
    public string? AlertType { get; init; }
    public string? Description { get; init; }
    public string? Severity { get; init; }
    public string? Origin { get; init; }
    public string? Detail { get; init; }
    public string? ActionGroup { get; init; }
    public string? KqlCode { get; init; }
    public string? TechnicalRequirement { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>Consulta KQL de la biblioteca (modelo de salida).</summary>
public sealed record KqlItem
{
    public int KqlId { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? KqlQuery { get; init; }
    public bool IsActive { get; init; }
}

// ---- DTOs de entrada (validacion equivalente a los modelos Pydantic) ----

public sealed class AlertCreate
{
    [Required, StringLength(300, MinimumLength = 1)]
    public string Name { get; set; } = "";

    public int? AlertNumber { get; set; }
    public string? Resource { get; set; }
    public string? AlertType { get; set; }
    public string? Description { get; set; }
    public string? Severity { get; set; }
    public string? Origin { get; set; }
    public string? Detail { get; set; }
    public string? ActionGroup { get; set; }
    public string? KqlCode { get; set; }
    public string? TechnicalRequirement { get; set; }
}

public sealed class KqlCreate
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = "";

    public string? Description { get; set; }
    public string? KqlQuery { get; set; }
}
