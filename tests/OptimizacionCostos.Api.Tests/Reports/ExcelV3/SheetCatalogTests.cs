using OptimizacionCostos.Api.Features.Reports.ExcelV3;
namespace OptimizacionCostos.Api.Tests.Reports.ExcelV3;
public class SheetCatalogTests
{
    [Fact]
    public void Todas_las_hojas_de_servicio_llevan_columnas_base_con_roles()
    {
        foreach (var (key, spec) in SheetCatalog.ServiceSheets())
        {
            var roles = spec.Columns.Select(c => c.Role).ToList();
            Assert.Contains(MoneyRole.Payg, roles);
            Assert.Contains(MoneyRole.Ri1, roles);
            Assert.Contains(MoneyRole.Ri3, roles);
            Assert.Contains(spec.Columns, c => c.Header == "Estado del cálculo");
            Assert.Contains(spec.Columns, c => c.Header == "Nota");
            Assert.True(spec.ComparableTotals, $"{key} sin totales comparables");
        }
    }

    [Fact]
    public void Nombres_de_hoja_en_espanol_y_unicos()
    {
        var names = SheetCatalog.ServiceSheets().Select(s => s.Spec.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("Optimización VMs", names);
        Assert.Contains("IP Pública", names);
        Assert.DoesNotContain(names, n => n.Contains("Managed Instance") && n.Contains("PAYG")); // nada en inglés
    }

    [Fact]
    public void Generica_sirve_para_synapse()
    {
        var spec = SheetCatalog.GenericServiceSheet("Synapse Dedicated Pool");
        Assert.Equal("Synapse Dedicated Pool", spec.Name);
        Assert.Contains(spec.Columns, c => c.Role == MoneyRole.Payg);
    }
}
