using OptimizacionCostos.Api.Auth;

namespace OptimizacionCostos.Api.Tests.Auth;

public sealed class ModulesTests
{
    [Fact]
    public void Catalogo_tiene_16_modulos_con_claves_unicas()
    {
        Assert.Equal(16, Modules.All.Count);
        Assert.Equal(Modules.All.Count, Modules.All.Select(m => m.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Claves_coinciden_con_las_secciones_del_front()
    {
        string[] esperadas =
        [
            "costos", "optimization", "service-catalog", "waf", "waf-ingestions",
            "waf-cost", "report", "boletin", "informe-valor", "reservations", "alerts", "policies", "consultants", "access-review",
            "pendientes-cdc", "pendientes-infra",
        ];
        Assert.Equal(esperadas, Modules.All.Select(m => m.Key).ToArray());
        Assert.All(esperadas, k => Assert.Contains(k, Modules.ValidKeys));
    }

    [Fact]
    public void Todos_los_modulos_tienen_etiqueta_y_grupo()
    {
        Assert.All(Modules.All, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Label));
            Assert.False(string.IsNullOrWhiteSpace(m.Group));
        });
    }
}
