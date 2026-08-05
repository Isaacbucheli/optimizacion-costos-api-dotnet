using System.ComponentModel.DataAnnotations;

namespace OptimizacionCostos.Api.Features.PolicyCatalog;

/// <summary>Politica del catalogo (modelo de salida). Las claves JSON salen en snake_case.</summary>
public sealed record PolicyItem
{
    public int PolicyId { get; init; }
    public int? PolicyNumber { get; init; }
    public string Name { get; init; } = "";
    public string? Category { get; init; }
    public string? PolicyType { get; init; }
    public string? RecommendedEffect { get; init; }
    public string? Mode { get; init; }
    public string? KeyParameters { get; init; }
    public string? Description { get; init; }
    public string? Objective { get; init; }
    public string? RecommendedScope { get; init; }
    public string? Rollout { get; init; }
    public string? Risk { get; init; }
    public string? ExampleParameters { get; init; }
    public string? AzureCli { get; init; }
    public string? Powershell { get; init; }
    public string? ScriptNotes { get; init; }
    public string? OfficialSource { get; init; }
    public bool IsActive { get; init; }
}

// ---- DTO de entrada (validacion equivalente al patron del catalogo de alertas) ----

public sealed class PolicyCreate
{
    [Required, StringLength(300, MinimumLength = 1)]
    public string Name { get; set; } = "";

    public int? PolicyNumber { get; set; }

    // Los StringLength son el ancho real de la columna en dbo.policy_catalog (espejo de
    // PolicyColumns.MaxLengths, que cubre el PUT). Sin ellos un valor más largo llega a SQL Server y
    // el INSERT muere con el error 8152 -> excepción sin manejar -> conexión cortada en vez de 400.
    // Las propiedades sin atributo corresponden a columnas NVARCHAR(MAX).
    [StringLength(160)] public string? Category { get; set; }
    [StringLength(80)] public string? PolicyType { get; set; }
    [StringLength(60)] public string? RecommendedEffect { get; set; }
    [StringLength(40)] public string? Mode { get; set; }
    [StringLength(300)] public string? KeyParameters { get; set; }
    public string? Description { get; set; }
    public string? Objective { get; set; }
    [StringLength(200)] public string? RecommendedScope { get; set; }
    public string? Rollout { get; set; }
    public string? Risk { get; set; }
    public string? ExampleParameters { get; set; }
    public string? AzureCli { get; set; }
    public string? Powershell { get; set; }
    public string? ScriptNotes { get; set; }
    [StringLength(500)] public string? OfficialSource { get; set; }
}
