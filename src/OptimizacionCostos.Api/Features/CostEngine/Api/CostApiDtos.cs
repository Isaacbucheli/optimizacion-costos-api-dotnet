using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.CostEngine.Api;

/// <summary>
/// Body opcional de POST /analysis/{id}/calculate. Port de CalculateRequest (Pydantic).
/// Las validaciones (ge/le) se aplican en el controller.
/// </summary>
public sealed class CalculateRequest
{
    /// <summary>Lista de service_key a calcular. null = todos los activos.</summary>
    public List<string>? ServiceKeys { get; set; }

    /// <summary>Si true, despues del calculo arma los 4 escenarios.</summary>
    public bool AutoBuildScenarios { get; set; } = true;

    /// <summary>Offset de recursos para calculo por bloques (>= 0).</summary>
    public int ResourceOffset { get; set; }

    /// <summary>Cantidad maxima de recursos a calcular en este bloque (1..200).</summary>
    public int? ResourceLimit { get; set; }

    /// <summary>Si true borra resultados previos del servicio antes de insertar.</summary>
    public bool ReplaceExisting { get; set; } = true;
}

/// <summary>Body de PUT /cost-results/{id}/manual-cost. Port de ManualCostRequest.</summary>
public sealed class ManualCostRequest
{
    public double? ManualMonthlyCost { get; set; }
    public string? ManualCostNote { get; set; }
}

/// <summary>
/// Una fila de GET /analysis/{id}/results. Refleja el SELECT grande de get_results:
/// cost_results JOIN azure_resources LEFT JOIN vm_power_usage. Sale en snake_case.
/// Los numericos se exponen como double? para que un valor nulo de BD salga como null.
/// </summary>
public sealed class CostResultRow
{
    public int CostResultId { get; set; }
    public int ResourceId { get; set; }
    public string? ServiceKey { get; set; }

    public string? ResourceName { get; set; }
    public string? ResourceType { get; set; }
    public string? Location { get; set; }
    public string? SubscriptionName { get; set; }
    public string? ResourceGroup { get; set; }
    public string? AzureResourceId { get; set; }

    public double? PaygHourly { get; set; }
    public double? PaygMonthly { get; set; }
    // La política snake_case serializa "Ri1yMonthly" como "ri1y_monthly"; el FastAPI/front
    // usan "ri_1y_monthly". Forzamos el nombre exacto para mantener el contrato.
    [JsonPropertyName("ri_1y_monthly")] public double? Ri1yMonthly { get; set; }
    [JsonPropertyName("ri_3y_monthly")] public double? Ri3yMonthly { get; set; }
    [JsonPropertyName("savings_1y_pct")] public double? Savings1yPct { get; set; }
    [JsonPropertyName("savings_3y_pct")] public double? Savings3yPct { get; set; }
    [JsonPropertyName("savings_1y_monthly")] public double? Savings1yMonthly { get; set; }
    [JsonPropertyName("savings_3y_monthly")] public double? Savings3yMonthly { get; set; }
    public double? SqlAddonMonthly { get; set; }
    public double? AhbDiscountMonthly { get; set; }
    public double? StorageMonthly { get; set; }

    public string? CalculationStatus { get; set; }
    public bool? IsVariablePricing { get; set; }
    public bool? IsManualCost { get; set; }
    public double? ManualMonthlyCost { get; set; }
    public string? ManualCostNote { get; set; }

    public bool? RiApplies { get; set; }
    public string? RiNotApplicableReason { get; set; }
    public string? RiCoverage { get; set; }  // nvarchar en BD ("confirmed"/"estimated"), NO numérico
    public string? RiReservationName { get; set; }
    public string? RiTerm { get; set; }

    public double? PowerRunningHours { get; set; }
    public double? PowerUptimePct { get; set; }
    public string? PowerPeriodStart { get; set; }
    public string? PowerPeriodEnd { get; set; }

    public string? CalculationNotes { get; set; }
    public string? CalculatedAt { get; set; }
}

/// <summary>Config de un escenario tal como lo expone GET /analysis/{id}/scenarios.</summary>
public sealed class ScenarioConfigDto
{
    [JsonPropertyName("use_ri_1y")] public bool UseRi1y { get; set; }
    [JsonPropertyName("use_ri_3y")] public bool UseRi3y { get; set; }
    public bool EliminateStoppedVmDisks { get; set; }
    public bool EliminateOrphanDisks { get; set; }
    public bool EliminateOrphanIps { get; set; }
}

/// <summary>Una linea del breakdown de un escenario (scenario_breakdown).</summary>
public sealed class ScenarioBreakdownDto
{
    public string? ServiceKey { get; set; }
    public string? LineLabel { get; set; }
    public double MonthlyCost { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Un escenario calculado tal como lo devuelve GET /analysis/{id}/scenarios
/// (cost_scenarios + scenario_breakdown). Estructura con "config" y "breakdown".
/// </summary>
public sealed class ScenarioReadDto
{
    public int ScenarioId { get; set; }
    public int Number { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public double TotalMonthly { get; set; }
    public double TotalAnnual { get; set; }
    public double SavingsMonthly { get; set; }
    public double SavingsAnnual { get; set; }
    public double SavingsPct { get; set; }
    public ScenarioConfigDto Config { get; set; } = new();
    public string? CalculatedAt { get; set; }
    public List<ScenarioBreakdownDto> Breakdown { get; set; } = [];
}

/// <summary>Una fila del resumen del cache de precios (GET /prices/cache-status).</summary>
public sealed class PriceCacheStatusRow
{
    public string? ServiceName { get; set; }
    public string? Region { get; set; }
    public int PricesCount { get; set; }
    public string? OldestCached { get; set; }
    public string? NewestCached { get; set; }
    public bool IsFresh { get; set; }
}
