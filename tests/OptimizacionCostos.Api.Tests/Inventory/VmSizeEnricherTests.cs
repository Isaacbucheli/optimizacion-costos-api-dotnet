using System.Net;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Features.Inventory;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Inventory;

/// <summary>
/// VmSizeEnricher: trae el conteo real de vCores de Microsoft.Compute/skus y lo estampa en las filas
/// de Resource Graph antes del insert, para que vm_details.vcpu_count deje de ser NULL.
///
/// Lo crítico es que use la capacidad <c>vCPUsAvailable</c> y no <c>vCPUs</c>: en un tamaño de núcleo
/// restringido esa es la diferencia entre licenciar SQL Server por 16 vCores y por 32. HTTP mockeado
/// con HttpMessageHandler falso; token con DelegatedTokenCredential.
/// </summary>
public sealed class VmSizeEnricherTests
{
    private static readonly TokenCredential FakeCred =
        DelegatedTokenCredential.Create((_, _) => new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static VmSizeEnricher NewEnricher(FakeHandler handler)
        => new(new FakeHttpClientFactory(handler), NullLogger<VmSizeEnricher>.Instance);

    private static JsonNode VmRow(string name, string size, string location = "eastus2", string sub = "sub-1")
        => new JsonObject
        {
            ["id"] = $"/subscriptions/{sub}/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/{name}",
            ["name"] = name,
            ["subscriptionId"] = sub,
            ["location"] = location,
            ["vmSize"] = size,
        };

    /// <summary>Forma real de la respuesta de Microsoft.Compute/skus (recortada a lo que se lee).</summary>
    private static HttpResponseMessage SkusResponse(params (string Name, int? Available, int? Vcpus, double? MemoryGb)[] skus)
    {
        var value = new JsonArray();
        foreach (var (name, available, vcpus, memory) in skus)
        {
            var caps = new JsonArray();
            if (vcpus is not null) caps.Add(new JsonObject { ["name"] = "vCPUs", ["value"] = vcpus.Value.ToString() });
            if (available is not null) caps.Add(new JsonObject { ["name"] = "vCPUsAvailable", ["value"] = available.Value.ToString() });
            if (memory is not null) caps.Add(new JsonObject { ["name"] = "MemoryGB", ["value"] = memory.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            caps.Add(new JsonObject { ["name"] = "MaxDataDiskCount", ["value"] = "32" });
            value.Add(new JsonObject
            {
                ["resourceType"] = "virtualMachines",
                ["name"] = name,
                ["capabilities"] = caps,
            });
        }
        // Ruido que la API devuelve de verdad y hay que ignorar.
        value.Add(new JsonObject { ["resourceType"] = "disks", ["name"] = "Premium_LRS" });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject { ["value"] = value }.ToJsonString()),
        };
    }

    // ------------------------------------------------------------------------------------
    // El caso que motivó todo: núcleo restringido, vCPUsAvailable gana sobre vCPUs.
    // ------------------------------------------------------------------------------------
    [Fact]
    public async Task Usa_vCPUsAvailable_en_nucleo_restringido()
    {
        var handler = new FakeHandler(_ => SkusResponse(
            ("Standard_E32-16s_v3", 16, 32, 256.0),
            ("Standard_D8s_v3", 8, 8, 32.0)));
        var rows = new List<JsonNode> { VmRow("sql01", "Standard_E32-16s_v3"), VmRow("app01", "Standard_D8s_v3") };

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, rows, CancellationToken.None);

        Assert.Equal(2, result.Stamped);
        Assert.Empty(result.Warnings);
        Assert.Equal(16, new RgRow(rows[0]).Int("vcpuCount"));   // NO 32
        Assert.Equal(256.0, new RgRow(rows[0]).Dbl("memoryGb"));
        Assert.Equal(8, new RgRow(rows[1]).Int("vcpuCount"));
    }

    [Fact]
    public async Task Si_no_viene_vCPUsAvailable_usa_vCPUs()
    {
        var handler = new FakeHandler(_ => SkusResponse(("Standard_D8s_v3", null, 8, 32.0)));
        var rows = new List<JsonNode> { VmRow("app01", "Standard_D8s_v3") };

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, rows, CancellationToken.None);

        Assert.Equal(1, result.Stamped);
        Assert.Equal(8, new RgRow(rows[0]).Int("vcpuCount"));
    }

    [Fact]
    public async Task Una_llamada_por_suscripcion_y_region()
    {
        var handler = new FakeHandler(_ => SkusResponse(("Standard_D8s_v3", 8, 8, 32.0)));
        var rows = new List<JsonNode>
        {
            VmRow("a", "Standard_D8s_v3", "eastus2", "sub-1"),
            VmRow("b", "Standard_D8s_v3", "eastus2", "sub-1"),   // misma pareja: no repite llamada
            VmRow("c", "Standard_D8s_v3", "centralus", "sub-1"),
            VmRow("d", "Standard_D8s_v3", "eastus2", "sub-2"),
        };

        await NewEnricher(handler).EnrichAsync(FakeCred, rows, CancellationToken.None);

        Assert.Equal(3, handler.Urls.Count);
        Assert.All(handler.Urls, u => Assert.Contains("Microsoft.Compute/skus", u));
        Assert.Contains(handler.Urls, u => u.Contains("sub-2"));
    }

    [Fact]
    public async Task Sigue_el_nextLink()
    {
        var page = 0;
        var handler = new FakeHandler(_ =>
        {
            page++;
            if (page == 1)
            {
                var first = SkusResponse(("Standard_D8s_v3", 8, 8, 32.0));
                var body = first.Content.ReadAsStringAsync().Result;
                var obj = JsonNode.Parse(body)!.AsObject();
                obj["nextLink"] = "https://management.azure.com/next-page";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(obj.ToJsonString()) };
            }
            return SkusResponse(("Standard_E32-16s_v3", 16, 32, 256.0));
        });
        var rows = new List<JsonNode> { VmRow("sql01", "Standard_E32-16s_v3") };

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, rows, CancellationToken.None);

        Assert.Equal(2, handler.Urls.Count);
        Assert.Equal(1, result.Stamped);
        Assert.Equal(16, new RgRow(rows[0]).Int("vcpuCount"));
    }

    // ------------------------------------------------------------------------------------
    // Nada de ceros silenciosos: lo que no se pudo resolver sale como advertencia.
    // ------------------------------------------------------------------------------------
    [Fact]
    public async Task Tamano_ausente_del_catalogo_queda_sin_estampar_y_con_advertencia()
    {
        var handler = new FakeHandler(_ => SkusResponse(("Standard_D8s_v3", 8, 8, 32.0)));
        var rows = new List<JsonNode> { VmRow("raro", "Standard_TamanoQueNoExiste") };

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, rows, CancellationToken.None);

        Assert.Equal(0, result.Stamped);
        Assert.Null(new RgRow(rows[0]).Int("vcpuCount"));
        Assert.Contains(result.Warnings, w => w.Contains("Standard_TamanoQueNoExiste"));
    }

    [Fact]
    public async Task Fallo_de_arm_no_tumba_la_importacion_pero_avisa()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var rows = new List<JsonNode> { VmRow("app01", "Standard_D8s_v3") };

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, rows, CancellationToken.None);

        Assert.Equal(0, result.Stamped);
        Assert.Null(new RgRow(rows[0]).Int("vcpuCount"));   // el cálculo caerá al respaldo por nombre
        Assert.Contains(result.Warnings, w => w.Contains("catálogo de tamaños"));
    }

    [Fact]
    public async Task Sin_filas_no_llama_a_arm()
    {
        var handler = new FakeHandler(_ => SkusResponse());

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, [], CancellationToken.None);

        Assert.Equal(0, result.Stamped);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task Fila_sin_subscriptionId_ni_location_no_rompe()
    {
        var handler = new FakeHandler(_ => SkusResponse(("Standard_D8s_v3", 8, 8, 32.0)));
        var rows = new List<JsonNode> { new JsonObject { ["name"] = "huerfana", ["vmSize"] = "Standard_D8s_v3" } };

        var result = await NewEnricher(handler).EnrichAsync(FakeCred, rows, CancellationToken.None);

        Assert.Equal(0, result.Stamped);
        Assert.Empty(handler.Urls);
        Assert.Contains(result.Warnings, w => w.Contains("subscriptionId"));
    }
}
