using System.Text.Json.Nodes;
using OptimizacionCostos.Api.Features.Boletin;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Tests.Boletin;

public class BoletinParsersTests
{
    private static RgRow Row(string json) => new(JsonNode.Parse(json));

    [Fact]
    public void ParseaFilaDeAdvisor()
    {
        var row = Row("""
        {
          "subscriptionId": "sub-1",
          "resourceId": "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Network/publicIPAddresses/ip1",
          "impactedField": "MICROSOFT.NETWORK/PUBLICIPADDRESSES",
          "impactedValue": "ip1",
          "problem": "Basic SKU public IP addresses will be retired",
          "solution": "Migrate to Standard SKU",
          "retirementDate": "2025-09-30",
          "retiringFeature": "Basic SKU",
          "learnMore": "https://aka.ms/basicip"
        }
        """);

        var r = BoletinParsers.FromAdvisorRow(row);

        Assert.NotNull(r);
        Assert.Equal(RetirementRow.SourceAdvisor, r!.Source);
        Assert.Equal("Basic SKU", r.AnnouncementKey);
        Assert.Equal("sub-1", r.SubscriptionId);
        Assert.Equal("ip1", r.ResourceName);
        Assert.Equal(new DateOnly(2025, 9, 30), r.RetirementDate);
        Assert.Equal("Migrate to Standard SKU", r.RecommendedAction);
        Assert.EndsWith("/publicIPAddresses/ip1", r.AzureResourceId);
    }

    [Fact]
    public void AdvisorSinFeatureOSinSuscripcionSeDescarta()
    {
        Assert.Null(BoletinParsers.FromAdvisorRow(Row("""{ "subscriptionId": "sub-1", "retiringFeature": "" }""")));
        Assert.Null(BoletinParsers.FromAdvisorRow(Row("""{ "retiringFeature": "Basic SKU" }""")));
    }

    [Fact]
    public void ParseaFilaDeServiceHealth()
    {
        var row = Row("""
        {
          "subscriptionId": "sub-2",
          "trackingId": "ABCD-123",
          "title": "Azure Virtual Desktop (classic) will be retired",
          "summary": "<p>detalle html</p>",
          "impactMitigationTime": "2026-09-30T00:00:00Z"
        }
        """);

        var r = BoletinParsers.FromHealthRow(row);

        Assert.NotNull(r);
        Assert.Equal(RetirementRow.SourceServiceHealth, r!.Source);
        Assert.Equal("ABCD-123", r.AnnouncementKey);
        Assert.Null(r.AzureResourceId);
        Assert.Equal(new DateOnly(2026, 9, 30), r.RetirementDate);
        Assert.Equal("Azure Virtual Desktop (classic) will be retired", r.Title);
    }

    [Fact]
    public void FechaIlegibleQuedaNull()
    {
        var r = BoletinParsers.FromHealthRow(Row("""
        { "subscriptionId": "s", "trackingId": "T", "title": "t", "impactMitigationTime": "no-es-fecha" }
        """));
        Assert.NotNull(r);
        Assert.Null(r!.RetirementDate);
    }

    [Fact]
    public void FingerprintEsEstableEInsensibleAMayusculasDelResourceId()
    {
        var a = new RetirementRow(RetirementRow.SourceAdvisor, "Basic SKU", "sub-1",
            "/SUBSCRIPTIONS/S/PROVIDERS/X/Y", "y", "X/Y", "Basic SKU", null, "t", null, null, null);
        var b = a with { AzureResourceId = "/subscriptions/s/providers/x/y" };

        Assert.Equal(Convert.ToHexString(a.Fingerprint(7)), Convert.ToHexString(b.Fingerprint(7)));
        Assert.NotEqual(Convert.ToHexString(a.Fingerprint(7)), Convert.ToHexString(a.Fingerprint(8)));
        Assert.Equal(32, a.Fingerprint(7).Length);
    }

    /// <summary>Hash dorado: el fingerprint es un contrato PERSISTIDO en dbo.boletin_retirement.
    /// Este test fija el ORDEN literal de los campos concatenados; si algún día cambia el formato
    /// interno (a propósito), hay que actualizar este test sabiendo que re-huellea todo lo guardado.</summary>
    [Fact]
    public void FingerprintTieneHashDoradoEstable()
    {
        var row = new RetirementRow(RetirementRow.SourceAdvisor, "Basic SKU", "sub-1",
            "/Subs/S/Providers/X/Y", "y", "X/Y", "Basic SKU", null, "t", null, null, null);
        var esperado = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("7|advisor|sub-1|Basic SKU|/subs/s/providers/x/y")));
        Assert.Equal(esperado, Convert.ToHexString(row.Fingerprint(7)));
    }

    // -------------------- FromHealthImpactedRow (A1) --------------------

    [Fact]
    public void ParseaFilaDeRecursoImpactadoDeServiceHealthYExtraeTrackingIdDelId()
    {
        var row = Row("""
        {
          "id": "/subscriptions/sub-2/providers/Microsoft.ResourceHealth/events/ABCD-123/impactedResources/0",
          "subscriptionId": "sub-2",
          "targetResourceId": "/subscriptions/sub-2/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1",
          "targetResourceType": "microsoft.compute/virtualmachines",
          "resourceName": "vm1"
        }
        """);

        var r = BoletinParsers.FromHealthImpactedRow(row);

        Assert.NotNull(r);
        Assert.Equal("ABCD-123", r!.TrackingId);
        Assert.Equal("sub-2", r.SubscriptionId);
        Assert.Equal("/subscriptions/sub-2/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1", r.ResourceId);
        Assert.Equal("vm1", r.ResourceName);
        Assert.Equal("microsoft.compute/virtualmachines", r.ResourceType);
    }

    [Theory]
    [InlineData("""{ "subscriptionId": "sub-2", "targetResourceId": "/x" }""")] // sin id → sin trackingId
    [InlineData("""{ "id": "/subscriptions/sub-2/providers/Microsoft.ResourceHealth/events/T/impactedResources/0", "targetResourceId": "/x" }""")] // sin subscriptionId
    [InlineData("""{ "id": "/subscriptions/sub-2/providers/Microsoft.ResourceHealth/events/T/impactedResources/0", "subscriptionId": "sub-2" }""")] // sin targetResourceId
    [InlineData("""{ "id": "/subscriptions/sub-2/providers/algo-sin-events", "subscriptionId": "sub-2", "targetResourceId": "/x" }""")] // id sin '/events/'
    public void RecursoImpactadoSinCampoClaveSeDescarta(string json) =>
        Assert.Null(BoletinParsers.FromHealthImpactedRow(Row(json)));

    [Fact]
    public void RecursoImpactadoConResourceNameFaltanteUsaUltimoSegmentoDelResourceId()
    {
        // Cobertura defensiva: el nombre exacto de la propiedad "resourceName" no está confirmado
        // contra datos reales de Azure (ver reporte); si no viene, se deriva del resourceId.
        var row = Row("""
        {
          "id": "/subscriptions/sub-2/providers/Microsoft.ResourceHealth/events/ABCD-123/impactedResources/0",
          "subscriptionId": "sub-2",
          "targetResourceId": "/subscriptions/sub-2/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1"
        }
        """);

        var r = BoletinParsers.FromHealthImpactedRow(row);

        Assert.NotNull(r);
        Assert.Equal("vm1", r!.ResourceName);
    }

    // -------------------- ExpandHealthRows (A1) --------------------

    private static RetirementRow HealthRow(string trackingId, string subId) => new(
        RetirementRow.SourceServiceHealth, trackingId, subId, null, "", "", "",
        new DateOnly(2026, 9, 30), "Título " + trackingId, "resumen", null, null);

    [Fact]
    public void AvisoConDosRecursosGeneraDosFilasResourceLevelYNingunaSubLevel()
    {
        var healthRows = new List<RetirementRow> { HealthRow("TRACK-1", "sub-1") };
        var impacted = new List<HealthImpactedResource>
        {
            new("TRACK-1", "sub-1", "/r/vm1", "vm1", "microsoft.compute/virtualmachines"),
            new("TRACK-1", "sub-1", "/r/vm2", "vm2", "microsoft.compute/virtualmachines"),
        };

        var result = BoletinParsers.ExpandHealthRows(healthRows, impacted);

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal("TRACK-1", r.AnnouncementKey));
        Assert.All(result, r => Assert.Equal("Título TRACK-1", r.Title)); // conserva título/fecha del aviso
        Assert.Contains(result, r => r.AzureResourceId == "/r/vm1" && r.ResourceName == "vm1");
        Assert.Contains(result, r => r.AzureResourceId == "/r/vm2" && r.ResourceName == "vm2");
    }

    [Fact]
    public void AvisoSinRecursosMantieneFilaSubLevel()
    {
        var healthRows = new List<RetirementRow> { HealthRow("TRACK-2", "sub-1") };

        var result = BoletinParsers.ExpandHealthRows(healthRows, []);

        Assert.Single(result);
        Assert.Null(result[0].AzureResourceId);
        Assert.Equal("TRACK-2", result[0].AnnouncementKey);
    }

    [Fact]
    public void RecursosDeOtraSuscripcionNoSeCruzan()
    {
        var healthRows = new List<RetirementRow> { HealthRow("TRACK-3", "sub-1") };
        // El recurso pertenece a la MISMA trackingId pero OTRA suscripción: no debe cruzarse.
        var impacted = new List<HealthImpactedResource>
        {
            new("TRACK-3", "sub-2", "/r/vmX", "vmX", "microsoft.compute/virtualmachines"),
        };

        var result = BoletinParsers.ExpandHealthRows(healthRows, impacted);

        Assert.Single(result);
        Assert.Null(result[0].AzureResourceId); // sigue siendo la fila sub-level de sub-1
    }
}
