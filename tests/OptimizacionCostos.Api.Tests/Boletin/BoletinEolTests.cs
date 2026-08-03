using System.Text.Json.Nodes;
using OptimizacionCostos.Api.Features.Boletin;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Tests.Boletin;

public class BoletinEolTests
{
    private static RgRow Row(string json) => new(JsonNode.Parse(json));

    private static LifecycleEntry Entry(string clave, string producto, string field, string pattern,
        DateOnly eos, string categoria = "so") =>
        new(1, clave, producto, categoria, field, pattern, eos, "Recomendación " + producto, null, true);

    [Fact]
    public void ParseaVmConOsNameYVersion()
    {
        var r = BoletinEol.FromVmOsRow(Row("""
            { "subscriptionId": "s1", "vmId": "/subs/s1/vms/srv01", "name": "srv01",
              "osName": "Windows Server 2012 R2 Standard", "osVersion": "Microsoft Windows NT 6.3.9600.0" }
            """));
        Assert.NotNull(r);
        Assert.Equal("windows server 2012 r2 standard microsoft windows nt 6.3.9600.0", r!.Haystack);
        Assert.Equal("os_name", r.MatchField);
        Assert.Equal("Microsoft.Compute/virtualMachines", r.ResourceType);
    }

    [Fact]
    public void VmSinOsNameSeIgnora() =>
        Assert.Null(BoletinEol.FromVmOsRow(Row("""
            { "subscriptionId": "s1", "vmId": "/subs/s1/vms/x", "name": "x", "osName": "", "osVersion": "" }
            """)));

    [Fact]
    public void ParseaSqlVm()
    {
        var r = BoletinEol.FromSqlVmRow(Row("""
            { "subscriptionId": "s1", "sqlVmId": "/subs/s1/sqlvms/db01", "name": "db01", "sqlImageOffer": "SQL2012-WS2012R2" }
            """));
        Assert.NotNull(r);
        Assert.Equal("sql2012-ws2012r2", r!.Haystack);
        Assert.Equal("sql_image_offer", r.MatchField);
    }

    [Fact]
    public void ElPatronMasLargoGanaPorRecurso()
    {
        // Una VM WS2012 R2 matchea "windows server 2012" Y "windows server 2012 r2": debe emitir SOLO la R2.
        var entries = new List<LifecycleEntry>
        {
            Entry("windows-server-2012", "Windows Server 2012", "os_name", "windows server 2012", new DateOnly(2023, 10, 10)),
            Entry("windows-server-2012-r2", "Windows Server 2012 R2", "os_name", "windows server 2012 r2", new DateOnly(2023, 10, 10)),
        };
        var vm = new EolResource("s1", "/subs/s1/vms/srv01", "srv01",
            "Microsoft.Compute/virtualMachines", "windows server 2012 r2 standard nt 6.3", "os_name");

        var rows = BoletinEol.MatchResources(entries, [vm]);

        var row = Assert.Single(rows);
        Assert.Equal("windows-server-2012-r2", row.AnnouncementKey);
        Assert.Equal(RetirementRow.SourceEol, row.Source);
        Assert.Equal(new DateOnly(2023, 10, 10), row.RetirementDate);
        Assert.False(row.Derived);
        Assert.Equal("Windows Server 2012 R2", row.RetiringFeature);
        Assert.StartsWith("Windows Server 2012 R2", row.Title);
    }

    [Fact]
    public void UnaVmPuedeMatchearSoYbdALaVez()
    {
        // El mismo servidor: su VM matchea WS2012R2 (os_name) y su SQL VM matchea SQL2012 (sql_image_offer).
        var entries = new List<LifecycleEntry>
        {
            Entry("windows-server-2012-r2", "Windows Server 2012 R2", "os_name", "windows server 2012 r2", new DateOnly(2023, 10, 10)),
            Entry("sql-server-2012", "SQL Server 2012", "sql_image_offer", "sql2012", new DateOnly(2022, 7, 12), "bd"),
        };
        var recursos = new List<EolResource>
        {
            new("s1", "/subs/s1/vms/db01", "db01", "Microsoft.Compute/virtualMachines", "windows server 2012 r2 standard", "os_name"),
            new("s1", "/subs/s1/sqlvms/db01", "db01", "Microsoft.SqlVirtualMachine/sqlVirtualMachines", "sql2012-ws2012r2", "sql_image_offer"),
        };

        var rows = BoletinEol.MatchResources(entries, recursos);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.AnnouncementKey == "windows-server-2012-r2");
        Assert.Contains(rows, r => r.AnnouncementKey == "sql-server-2012");
    }

    [Fact]
    public void SinMatchNoEmiteNada()
    {
        var entries = new List<LifecycleEntry> { Entry("centos-7", "CentOS 7", "os_name", "centos 7", new DateOnly(2024, 6, 30)) };
        var vm = new EolResource("s1", "/subs/s1/vms/u", "u", "Microsoft.Compute/virtualMachines", "ubuntu 22.04", "os_name");
        Assert.Empty(BoletinEol.MatchResources(entries, [vm]));
    }
}
