using OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;
using OptimizacionCostos.Api.Features.Optimization;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Tarea 1 de la entrega 6: el mapeo de categorías de servicio para el acumulado de lo ejecutado.
/// La colección estática real de <see cref="OptimizationChecks"/> es la propiedad
/// <c>Registered</c> (no <c>All</c> ni <c>Definitions</c> como sugería el borrador del plan).
/// </summary>
public sealed class CategoriaEjecutadoTests
{
    /// <summary>El guardia que evita el defecto silencioso: un check nuevo sin mapeo caería al
    /// residual y el gráfico apilado mentiría sin que nadie lo note.</summary>
    [Fact]
    public void Todo_check_del_barrido_tiene_categoria()
    {
        var sinMapear = OptimizationChecks.Registered.Select(c => c.CheckId)
            .Where(id => !CategoriaEjecutado.PorCheck.ContainsKey(id)).ToList();
        Assert.True(sinMapear.Count == 0,
            "Checks sin categoría para el gráfico apilado: " + string.Join(", ", sinMapear));
    }

    /// <summary>La precedencia: reserva gana siempre; check antes que BITCOST; residual visible.</summary>
    [Fact]
    public void La_precedencia_resuelve_en_orden()
    {
        Assert.Equal(CategoriaEjecutado.Reservas, CategoriaEjecutado.Resolver("reserva", "cualquier-check", "Storage"));
        Assert.Equal(CategoriaEjecutado.Residual, CategoriaEjecutado.Resolver("barrido", "check-inexistente", null));
        Assert.Equal("Storage", CategoriaEjecutado.Resolver("matriz", null, "Storage"));
    }
}
