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

    // Los StringLength son el ancho real de la columna en dbo.alert_catalog (espejo de
    // AlertColumns.AlertMaxLengths, que cubre el PUT). Sin ellos un valor más largo llega a SQL
    // Server y el INSERT muere con el error 8152 -> excepción sin manejar -> conexión cortada en vez
    // de 400. Las propiedades sin atributo corresponden a columnas NVARCHAR(MAX).
    [StringLength(120)] public string? Resource { get; set; }
    [StringLength(80)] public string? AlertType { get; set; }
    public string? Description { get; set; }
    [StringLength(40)] public string? Severity { get; set; }
    [StringLength(120)] public string? Origin { get; set; }
    public string? Detail { get; set; }
    [StringLength(300)] public string? ActionGroup { get; set; }
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
