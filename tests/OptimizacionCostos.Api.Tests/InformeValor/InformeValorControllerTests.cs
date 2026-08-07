using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.InformeValor.Api;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// Gating declarativo del controller: la clase exige el módulo a nivel View (para el estado) y
/// las dos mutaciones (Subir/Borrar) suben a Edit. Espejo de las pruebas de atributos de otros
/// controllers por cliente; el comportamiento real (orden de validaciones) va en
/// InformeValorUploadApiTests.
/// </summary>
public sealed class InformeValorControllerTests
{
    private static MethodInfo Method(string name) =>
        typeof(InformeValorController).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;

    [Fact]
    public void La_clase_esta_gateada_por_el_modulo()
    {
        var attr = typeof(InformeValorController).GetCustomAttribute<RequireModuleAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(Modules.InformeValor, attr!.ModuleKey);
        Assert.Equal(ModuleAccess.View, attr.Access);
    }

    [Theory]
    [InlineData("Subir")]
    [InlineData("Borrar")]
    public void Las_mutaciones_exigen_permiso_de_edicion(string metodo)
    {
        var attr = Method(metodo).GetCustomAttribute<RequireModuleAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(ModuleAccess.Edit, attr!.Access);
    }

    [Fact]
    public void El_estado_no_exige_permiso_de_edicion()
    {
        Assert.Null(Method("Estado").GetCustomAttribute<RequireModuleAttribute>());
    }
}
