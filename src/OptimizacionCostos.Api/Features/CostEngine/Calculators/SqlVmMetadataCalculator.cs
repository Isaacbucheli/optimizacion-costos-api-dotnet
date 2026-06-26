using OptimizacionCostos.Api.Features.CostEngine.Pricing;

namespace OptimizacionCostos.Api.Features.CostEngine.Calculators;

/// <summary>
/// "Calculadora" de SQL Virtual Machines. Port 1:1 de
/// app/calculators/sql_vm_metadata.py::SqlVmMetadataCalculator (service_key "sql_vm").
///
/// Las SQL VMs son metadata sobre VMs que ya tienen costo calculado en
/// ComputeVMCalculator. Aquí no se calcula nada: cada recurso se marca como
/// not_applicable. La integración real está en ComputeVMCalculator, que hace JOIN con
/// sql_vm_details vía vm_azure_resource_id y suma el SQL add-on.
/// </summary>
public sealed class SqlVmMetadataCalculator : ICostCalculator
{
    // Se inyectan por consistencia con las demás calculadoras (firma uniforme),
    // aunque esta calculadora no consulta precios ni constantes (no costea nada).
    private readonly IPriceRepository _prices;
    private readonly IPricingConstants _constants;

    public SqlVmMetadataCalculator(IPriceRepository prices, IPricingConstants constants)
    {
        _prices = prices;
        _constants = constants;
    }

    public IReadOnlyList<CostResult> Calculate(IReadOnlyList<ResourceRow> resources, int analysisId)
    {
        var results = new List<CostResult>();
        foreach (var r in resources)
        {
            var result = new CostResult(r.ResourceId, analysisId, "sql_vm")
            {
                CalculationStatus = "not_applicable",
                CalculationNotes = "SQL VM es metadata. El costo está sumado en la VM asociada.",
            };
            results.Add(result);
        }
        return results;
    }
}
