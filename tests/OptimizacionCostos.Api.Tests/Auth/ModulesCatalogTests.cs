using OptimizacionCostos.Api.Auth;

namespace OptimizacionCostos.Api.Tests.Auth;

public sealed class ModulesCatalogTests
{
    [Fact]
    public void Informe_valor_esta_en_el_catalogo_y_en_el_grupo_Informes()
    {
        Assert.Equal("informe-valor", Modules.InformeValor);
        var info = Modules.All.SingleOrDefault(m => m.Key == Modules.InformeValor);
        Assert.NotNull(info);
        Assert.Equal("Informes", info!.Group);
        Assert.Contains(Modules.InformeValor, Modules.ValidKeys);
    }

    [Fact]
    public void No_hay_claves_de_modulo_repetidas()
    {
        var dup = Modules.All.GroupBy(m => m.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dup);
    }
}
