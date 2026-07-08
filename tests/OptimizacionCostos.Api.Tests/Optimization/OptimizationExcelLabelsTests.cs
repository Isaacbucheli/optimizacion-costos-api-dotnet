using System.Text.Json;
using OptimizacionCostos.Api.Features.Optimization;

namespace OptimizacionCostos.Api.Tests.Optimization;

public class OptimizationExcelLabelsTests
{
    [Fact]
    public void Check_conocido_titulo_es_y_grupo()
    {
        Assert.Equal("Azure Hybrid Benefit no aplicado", OptimizationExcelLabels.CheckTitle("vms_without_ahb"));
        Assert.Equal("Azure Hybrid Benefit", OptimizationExcelLabels.CheckGroup("vms_without_ahb"));
        Assert.Equal("Storage", OptimizationExcelLabels.CheckGroup("orphaned_disks"));
        Assert.Equal("Networking", OptimizationExcelLabels.CheckGroup("basic_load_balancers"));
    }

    [Fact]
    public void Check_desconocido_cae_en_otros_con_el_id_como_titulo()
    {
        Assert.Equal("nuevo_check_x", OptimizationExcelLabels.CheckTitle("nuevo_check_x"));
        Assert.Equal("Otros", OptimizationExcelLabels.CheckGroup("nuevo_check_x"));
    }

    [Fact]
    public void Traducciones_de_severidad_y_estado()
    {
        Assert.Equal("Alta", OptimizationExcelLabels.SeverityLabel("high"));
        Assert.Equal("Media", OptimizationExcelLabels.SeverityLabel("medium"));
        Assert.Equal("Baja", OptimizationExcelLabels.SeverityLabel("low"));
        Assert.Equal("En progreso", OptimizationExcelLabels.StateLabel("en_progreso"));
        Assert.Equal("Abierto", OptimizationExcelLabels.StateLabel("abierto"));
        Assert.Equal("", OptimizationExcelLabels.StateLabel(null));
    }

    [Fact]
    public void Parsea_resource_group_del_resource_id_y_tolera_malformados()
    {
        var id = "/subscriptions/abc/resourceGroups/RG-PROD/providers/Microsoft.Compute/disks/d1";
        Assert.Equal("RG-PROD", OptimizationExcelLabels.ParseResourceGroup(id));
        Assert.Equal("", OptimizationExcelLabels.ParseResourceGroup("/subscriptions/abc"));
        Assert.Equal("", OptimizationExcelLabels.ParseResourceGroup(null));
    }

    [Fact]
    public void DetailText_desde_JsonElement_y_fallback_note()
    {
        var d1 = JsonSerializer.Deserialize<JsonElement>("""{"sku":"P10","diskSizeGB":128}""");
        Assert.Equal("P10 · 128 GB", OptimizationExcelLabels.DetailText(d1));
        var d2 = JsonSerializer.Deserialize<JsonElement>("""{"note":"sin backend"}""");
        Assert.Equal("sin backend", OptimizationExcelLabels.DetailText(d2));
        Assert.Equal("", OptimizationExcelLabels.DetailText(null));
    }

    [Fact]
    public void FilterByStates_filtra_y_vacio_devuelve_todo()
    {
        List<IReadOnlyDictionary<string, object?>> rows =
        [
            new Dictionary<string, object?> { ["state"] = "abierto" },
            new Dictionary<string, object?> { ["state"] = "resuelto" },
        ];
        Assert.Single(OptimizationExcelLabels.FilterByStates(rows, ["abierto"]));
        Assert.Equal(2, OptimizationExcelLabels.FilterByStates(rows, []).Count);
    }
}
