using System.Text.Json.Nodes;
using OptimizacionCostos.Api.Features.Boletin;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Tests.Boletin;

public class BoletinDetectorsTests
{
    [Theory]
    [InlineData("End of support notice: Support for Node.js 20 ends on 30 April 2026—upgrade your apps to Node.js 22", "node", "20")]
    [InlineData("Support for Python 3.9 is ending", "python", "3.9")]
    [InlineData("Support for PHP 8.1 in App Service ends", "php", "8.1")]
    [InlineData(".NET 8 (LTS) on App Service retires", "dotnet", "8")]
    public void MatcheaUnRuntimeDelTitulo(string title, string family, string version)
    {
        var targets = BoletinDetectors.MatchAnnouncement(title);
        Assert.Contains(targets, t => t.Family == family && t.Version == version);
    }

    [Fact]
    public void TituloMultiVersionEmiteVariosTargets()
    {
        var targets = BoletinDetectors.MatchAnnouncement(
            "Support for Python-2.7; 3.8 and PowerShell- 7.1; 7.2 will be retired on 30 September 2026");
        // Nota: los sueltos "3.8"/"7.2" sin familia adelante se capturan por la lista separada por ;/y/&
        Assert.Contains(targets, t => t is { Family: "python", Version: "2.7" });
        Assert.Contains(targets, t => t is { Family: "python", Version: "3.8" });
        Assert.Contains(targets, t => t is { Family: "powershell", Version: "7.1" });
        Assert.Contains(targets, t => t is { Family: "powershell", Version: "7.2" });
    }

    [Fact]
    public void TituloSinRuntimeNoMatchea() =>
        Assert.Empty(BoletinDetectors.MatchAnnouncement("Azure Virtual Desktop (classic) will be retired"));

    [Theory]
    [InlineData("node", "20", "NODE|20-lts", true)]
    [InlineData("node", "20", "Node|20", true)]
    [InlineData("node", "20", "NODE|18", false)]
    [InlineData("python", "3.9", "Python|3.9", true)]
    [InlineData("python", "3.9", "PYTHON|3.11", false)]
    [InlineData("dotnet", "8", "DOTNETCORE|8.0", true)]
    [InlineData("node", "20", "DOCKER|imagen:tag", false)]
    public void RuntimeMatchesNormalizaFamiliaYVersion(string fam, string ver, string runtime, bool expected) =>
        Assert.Equal(expected, BoletinDetectors.RuntimeMatches(new RuntimeTarget(fam, ver), runtime));

    [Fact]
    public void ParseaFilaLinuxDeArg()
    {
        var row = new RgRow(JsonNode.Parse("""
            { "subscriptionId": "s1", "siteId": "/subscriptions/s1/.../sites/mi-app",
              "name": "mi-app", "runtime": "NODE|20-lts", "siteKind": "app,linux" }
            """));
        var site = BoletinDetectors.FromLinuxSiteRow(row);
        Assert.NotNull(site);
        Assert.Equal("mi-app", site!.SiteName);
        Assert.Equal("NODE|20-lts", site.Runtime);
    }

    [Fact]
    public void BuildDerivedRowsEmiteSoloMatchesDeLaMismaSuscripcionYNoDuplicaExistentes()
    {
        var aviso = new RetirementRow(RetirementRow.SourceServiceHealth, "TRACK-1", "sub-1", null,
            "", "", "", new DateOnly(2026, 4, 30), "Support for Node.js 20 ends", "sum", null, null);
        var targets = new List<RuntimeTarget> { new("node", "20") };
        var sites = new List<SiteRuntime>
        {
            new("sub-1", "/subs/sub-1/sites/a", "a", "NODE|20-lts"),   // match
            new("sub-1", "/subs/sub-1/sites/b", "b", "PYTHON|3.11"),   // runtime distinto
            new("sub-2", "/subs/sub-2/sites/c", "c", "NODE|20"),       // otra suscripción
            new("sub-1", "/subs/sub-1/sites/d", "d", "NODE|20"),       // ya existente (enriquecimiento)
        };
        var existing = new HashSet<string>(StringComparer.Ordinal) { "/subs/sub-1/sites/d" };

        var rows = BoletinDetectors.BuildDerivedRows(aviso, targets, sites, existing);

        var r = Assert.Single(rows);
        Assert.Equal("/subs/sub-1/sites/a", r.AzureResourceId);
        Assert.True(r.Derived);
        Assert.Equal("TRACK-1", r.AnnouncementKey);          // mismo grupo que el aviso
        Assert.Equal("Microsoft.Web/sites", r.ResourceType);
    }
}
