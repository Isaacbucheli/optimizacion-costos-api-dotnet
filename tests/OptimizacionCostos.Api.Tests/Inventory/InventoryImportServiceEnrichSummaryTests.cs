using OptimizacionCostos.Api.Features.FinOpsData;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Tests.Inventory;

/// <summary>
/// Prueba pura (sin SQL) de InventoryImportService.EnrichSummary: agrega display_name y
/// finops_category a las filas del GROUP BY del summary, usando el mapa de tipos de FinOps ref data.
/// </summary>
public class InventoryImportServiceEnrichSummaryTests
{
    [Fact]
    public void EnrichSummary_AgregaDisplayNameYCategoria()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["service_category"] = "compute", ["resource_type"] = "microsoft.compute/virtualmachines", ["count"] = 5 },
        };
        var types = new Dictionary<string, FinOpsResourceTypeInfo>(StringComparer.OrdinalIgnoreCase)
        { ["microsoft.compute/virtualmachines"] = new("Virtual machine", "Compute") };

        var enriched = InventoryImportService.EnrichSummary(rows, types);

        Assert.Equal("Virtual machine", enriched[0]["display_name"]);
        Assert.Equal("Compute", enriched[0]["finops_category"]);
    }

    [Fact]
    public void EnrichSummary_TipoDesconocido_DejaCamposEnNull()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["service_category"] = "otros", ["resource_type"] = "microsoft.foo/bar", ["count"] = 1 },
        };
        var types = new Dictionary<string, FinOpsResourceTypeInfo>(StringComparer.OrdinalIgnoreCase);

        var enriched = InventoryImportService.EnrichSummary(rows, types);

        Assert.Null(enriched[0]["display_name"]);
        Assert.Null(enriched[0]["finops_category"]);
        // Las columnas originales se conservan.
        Assert.Equal("otros", enriched[0]["service_category"]);
        Assert.Equal(1, enriched[0]["count"]);
    }
}
