using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;

namespace OptimizacionCostos.Api.Tests.Auth;

public sealed class RequireModuleCoverageTests
{
    // Controllers de módulo → clave esperada a nivel CLASE.
    private static readonly Dictionary<string, string> ClassGated = new()
    {
        ["AlertCatalogController"] = Modules.Alerts,
        ["PolicyCatalogController"] = Modules.Policies,
        ["ConsultantsController"] = Modules.Consultants,
        ["CdcController"] = Modules.Reservations,
        ["AnalysisRefreshController"] = Modules.Costos,
        ["AnalysisController"] = Modules.Costos,
        ["CostCalculationController"] = Modules.Costos,
        ["AzureImportController"] = Modules.Costos,
        ["ExcelController"] = Modules.Costos,
        ["FinOpsDataController"] = Modules.Costos,
        ["OptimizationController"] = Modules.Optimization,
        ["ReportsController"] = Modules.Report,
        ["FilesController"] = Modules.Report,
        ["BoletinController"] = Modules.Boletin,
        ["InformeValorController"] = Modules.InformeValor,
    };

    private static IEnumerable<Type> Controllers() =>
        typeof(Modules).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t));

    [Fact]
    public void Controllers_de_modulo_tienen_RequireModule_a_nivel_clase()
    {
        foreach (var (name, expectedKey) in ClassGated)
        {
            var type = Controllers().SingleOrDefault(t => t.Name == name);
            Assert.True(type is not null, $"No existe el controller {name}");
            var attr = type!.GetCustomAttribute<RequireModuleAttribute>();
            Assert.True(attr is not null, $"{name} no tiene [RequireModule] a nivel clase");
            Assert.Equal(expectedKey, attr!.ModuleKey);
        }
    }

    [Fact]
    public void Todas_las_claves_usadas_en_atributos_existen_en_el_catalogo()
    {
        foreach (var type in Controllers())
        {
            var attrs = type.GetCustomAttributes<RequireModuleAttribute>()
                .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .SelectMany(m => m.GetCustomAttributes<RequireModuleAttribute>()));
            foreach (var attr in attrs)
                Assert.Contains(attr.ModuleKey, Modules.ValidKeys);
        }
    }

    [Fact]
    public void WafController_gatea_cost_reference_e_ingestions_con_su_modulo()
    {
        var waf = Controllers().Single(t => t.Name == "WafController");
        var byKey = waf.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<RequireModuleAttribute>().Select(a => (m.Name, a)))
            .ToList();
        Assert.Contains(byKey, x => x.a.ModuleKey == Modules.WafCost);
        Assert.Contains(byKey, x => x.a.ModuleKey == Modules.WafIngestions && x.a.Access == ModuleAccess.Edit);
        Assert.Contains(byKey, x => x.a.ModuleKey == Modules.Waf && x.a.Access == ModuleAccess.Edit);
    }

    [Fact]
    public void Waf_status_permanece_publico_sin_RequireModule()
    {
        var waf = Controllers().Single(t => t.Name == "WafController");
        var statusMethod = waf.GetMethod("Status", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotNull(statusMethod);

        // Status no debe tener RequireModuleAttribute
        var requireModuleAttr = statusMethod!.GetCustomAttribute<RequireModuleAttribute>();
        Assert.Null(requireModuleAttr);

        // Status debe tener AllowAnonymousAttribute
        var allowAnonymousAttr = statusMethod.GetCustomAttribute<AllowAnonymousAttribute>();
        Assert.NotNull(allowAnonymousAttr);
    }
}
