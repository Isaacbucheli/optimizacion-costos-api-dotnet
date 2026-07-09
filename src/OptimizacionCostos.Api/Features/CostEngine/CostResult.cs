namespace OptimizacionCostos.Api.Features.CostEngine;

/// <summary>
/// Resultado de cálculo de costo para un recurso. Port 1:1 de
/// app/calculators/base.py::CostResult (mismos campos, misma lógica de ahorros/descarte de RI).
///
/// calculation_status:
///   calculated       OK, todos los precios encontrados.
///   price_not_found  Falta precio en API. Costo NULL. Manual.
///   manual_required  Servicio requiere entrada manual (Cosmos).
///   variable_pricing Consumo variable (Y1/FC1/F1). Costo NULL.
///   not_running      Compute pausado = $0 (hoy solo Synapse Dedicated Pool pausado; las VMs apagadas se costean referenciales desde 2026-07-08).
///   not_applicable   No aplica cálculo (ej. SQL VM es metadata).
/// </summary>
public sealed class CostResult(int resourceId, int analysisId, string serviceKey)
{
    public int ResourceId { get; } = resourceId;
    public int AnalysisId { get; } = analysisId;
    public string ServiceKey { get; } = serviceKey;

    public double? PaygHourly { get; set; }
    public double? PaygMonthly { get; set; }
    public double? Ri1yMonthly { get; set; }
    public double? Ri3yMonthly { get; set; }

    public double? Savings1yPct { get; set; }
    public double? Savings3yPct { get; set; }
    public double? Savings1yMonthly { get; set; }
    public double? Savings3yMonthly { get; set; }

    public double? SqlAddonMonthly { get; set; }
    public double? AhbDiscountMonthly { get; set; }
    public double? StorageMonthly { get; set; }

    public string CalculationStatus { get; set; } = "calculated";
    public bool IsVariablePricing { get; set; }
    public bool IsManualCost { get; set; }
    public double? ManualMonthlyCost { get; set; }
    public string? ManualCostNote { get; set; }

    public bool RiApplies { get; set; }
    public string? RiNotApplicableReason { get; set; }

    public string? CalculationNotes { get; set; }

    /// <summary>MeterId del precio PAYG elegido (Retail API). Null si no hubo precio/DTO sin meter.</summary>
    public string? PaygMeterId { get; set; }

    /// <summary>"eligible" | "not_eligible" | "unknown" (ver RiEligibility). Lo asigna el enricher.</summary>
    public string? RiEligibility { get; set; }

    /// <summary>Calcula ahorros como fracción decimal a partir de payg/ri.</summary>
    public void ComputeSavings()
    {
        var @base = PaygMonthly;
        if (@base is null or <= 0) return;

        if (Ri1yMonthly is { } r1 && r1 < @base)
        {
            Savings1yMonthly = @base - r1;
            Savings1yPct = Savings1yMonthly / @base;
        }
        if (Ri3yMonthly is { } r3 && r3 < @base)
        {
            Savings3yMonthly = @base - r3;
            Savings3yPct = Savings3yMonthly / @base;
        }
    }

    /// <summary>Descarta RI no beneficiosas (RI &gt;= PAYG) para no mostrar ahorro falso.</summary>
    public void DiscardNonSavingRi()
    {
        var @base = PaygMonthly;
        if (@base is null or <= 0) return;

        var discarded = new List<string>();
        if (Ri1yMonthly is { } r1 && r1 >= @base)
        {
            discarded.Add("RI 1Y >= PAYG");
            Ri1yMonthly = null; Savings1yMonthly = null; Savings1yPct = null;
        }
        if (Ri3yMonthly is { } r3 && r3 >= @base)
        {
            discarded.Add("RI 3Y >= PAYG");
            Ri3yMonthly = null; Savings3yMonthly = null; Savings3yPct = null;
        }

        if (discarded.Count > 0)
        {
            var note = "RI descartada por precio no beneficioso: " + string.Join(", ", discarded);
            CalculationNotes = string.IsNullOrEmpty(CalculationNotes) ? note : $"{CalculationNotes}; {note}";
        }

        if (RiApplies && Ri1yMonthly is null && Ri3yMonthly is null)
        {
            RiApplies = false;
            RiNotApplicableReason = "No se encontro RI menor que PAYG";
        }
    }
}
