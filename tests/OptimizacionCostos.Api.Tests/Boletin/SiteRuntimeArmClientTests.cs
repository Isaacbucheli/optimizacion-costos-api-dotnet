using System.Text.Json;
using OptimizacionCostos.Api.Features.Boletin;

namespace OptimizacionCostos.Api.Tests.Boletin;

public class SiteRuntimeArmClientTests
{
    private static JsonElement Config(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ParseaRuntimesWindowsDeConfigWeb()
    {
        var props = Config("""
            { "properties": { "netFrameworkVersion": "v8.0", "phpVersion": "", "pythonVersion": null,
                              "nodeVersion": "~20", "powerShellVersion": "7.2", "linuxFxVersion": "" } }
            """);
        var runtimes = SiteRuntimeArmClient.ParseSiteConfig(
            new WindowsSiteRef("sub-1", "/subs/sub-1/sites/w1", "w1"), props);

        Assert.Contains(runtimes, r => r.Runtime == "DOTNET|8.0");
        Assert.Contains(runtimes, r => r.Runtime == "NODE|20");
        Assert.Contains(runtimes, r => r.Runtime == "POWERSHELL|7.2");
        Assert.DoesNotContain(runtimes, r => r.Runtime.StartsWith("PHP"));
        Assert.All(runtimes, r => Assert.Equal("/subs/sub-1/sites/w1", r.SiteId));
    }

    [Fact]
    public void NetFrameworkV4NoEsDotnetModerno()
    {
        // v4.0 es .NET Framework clásico: no debe reportarse como DOTNET|4.0 (los retiros
        // de ".NET N" son de .NET moderno; incluirlo generaría falsos positivos).
        var props = Config("""{ "properties": { "netFrameworkVersion": "v4.0" } }""");
        var runtimes = SiteRuntimeArmClient.ParseSiteConfig(new WindowsSiteRef("s", "/x", "x"), props);
        Assert.Empty(runtimes);
    }

    [Fact]
    public void ConfigVaciaNoEmiteNada()
    {
        var runtimes = SiteRuntimeArmClient.ParseSiteConfig(
            new WindowsSiteRef("s", "/x", "x"), Config("""{ "properties": {} }"""));
        Assert.Empty(runtimes);
    }
}
