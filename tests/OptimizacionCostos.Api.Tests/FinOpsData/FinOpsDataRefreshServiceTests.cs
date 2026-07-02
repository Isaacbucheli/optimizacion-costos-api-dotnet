using System.Net;
using OptimizacionCostos.Api.Features.FinOpsData;
using Xunit;

namespace OptimizacionCostos.Api.Tests.FinOpsData;

/// <summary>Fake del store: acumula datasets reemplazados y el log de refresh en memoria.</summary>
public sealed class FakeFinOpsDataStore : IFinOpsDataStore
{
    public Dictionary<string, IReadOnlyList<string[]>> Replaced { get; } = new();
    public List<(string Dataset, int Rows, string Status)> Log { get; } = new();
    public List<FinOpsRefreshStatus> Status { get; set; } = new();

    public Task ReplaceDatasetAsync(string key, IReadOnlyList<string[]> rows, CancellationToken ct)
    { Replaced[key] = rows; return Task.CompletedTask; }
    public Task RecordRefreshAsync(string key, int rowCount, string status, CancellationToken ct)
    { Log.Add((key, rowCount, status)); return Task.CompletedTask; }
    public Task<IReadOnlyList<FinOpsRefreshStatus>> GetStatusAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FinOpsRefreshStatus>>(Status);
}

/// <summary>HttpMessageHandler fake: sirve contenido por nombre de archivo.</summary>
public sealed class FakeCsvHandler(Dictionary<string, string> byFile) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var file = req.RequestUri!.Segments[^1];
        if (!byFile.TryGetValue(file, out var body))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}

public sealed class FinOpsDataRefreshServiceTests
{
    // CSV chiquitos pero con headers REALES de los datasets v14.
    private static Dictionary<string, string> HappyCsvs(int eligRows = 3) => new()
    {
        ["PricingUnits.csv"] = "\"UnitOfMeasure\",\"AccountTypes\",\"PricingBlockSize\",\"DistinctUnits\"\n\"1 Hour\",\"EA, MCA\",\"1\",\"Hours\"",
        ["Regions.csv"] = "\"OriginalValue\",\"RegionId\",\"RegionName\"\n\"us east 2\",\"eastus2\",\"East US 2\"",
        ["Services.csv"] = "\"ConsumedService\",\"ResourceType\",\"ServiceName\",\"ServiceCategory\",\"ServiceSubcategory\",\"PublisherName\",\"PublisherType\",\"Environment\",\"ServiceModel\"\n\"microsoft.compute\",\"microsoft.compute/virtualmachines\",\"Virtual Machines\",\"Compute\",\"Virtual Machines\",\"Microsoft\",\"Cloud Provider\",\"Cloud\",\"IaaS\"",
        ["ResourceTypes.csv"] = "\"ResourceType\",\"SingularDisplayName\",\"PluralDisplayName\",\"LowerSingularDisplayName\",\"LowerPluralDisplayName\",\"IsPreview\",\"Description\",\"Icon\",\"Links\"\n\"microsoft.compute/virtualmachines\",\"Virtual machine\",\"Virtual machines\",\"virtual machine\",\"virtual machines\",\"false\",\"Una VM\",\"icon.svg\",\"\"",
        ["CommitmentDiscountEligibility.csv"] = "\"MeterId\",\"x_CommitmentDiscountSpendEligibility\",\"x_CommitmentDiscountUsageEligibility\"\n"
            + string.Join("\n", Enumerable.Range(0, eligRows).Select(i => $"\"meter-{i}\",\"Eligible\",\"Not Eligible\"")),
    };

    private static FinOpsDataRefreshService Build(FakeFinOpsDataStore store, Dictionary<string, string> csvs, int minRowsOverride = 1)
    {
        var http = new HttpClient(new FakeCsvHandler(csvs));
        // minRowsOverride: los tests usan CSVs chicos; el servicio acepta los umbrales por dataset vía ctor para testear sanity.
        return new FinOpsDataRefreshService(http, store, minRowsOverride);
    }

    [Fact]
    public async Task RefreshAll_Ok_ReemplazaLos5YRegistraLog()
    {
        var store = new FakeFinOpsDataStore();
        var svc = Build(store, HappyCsvs());
        var results = await svc.RefreshAllAsync(CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal("ok", r.Status));
        Assert.Equal(5, store.Replaced.Count);
        Assert.Contains(store.Log, l => l.Dataset == "commitment_eligibility" && l.Status == "ok");
    }

    [Fact]
    public async Task RefreshAll_Http404_NoReemplazaYRegistraError()
    {
        var csvs = HappyCsvs();
        csvs.Remove("Regions.csv"); // 404
        var store = new FakeFinOpsDataStore();
        var svc = Build(store, csvs);
        var results = await svc.RefreshAllAsync(CancellationToken.None);

        var regions = results.Single(r => r.Dataset == "regions");
        Assert.StartsWith("error:", regions.Status);
        Assert.False(store.Replaced.ContainsKey("regions")); // conserva datos previos
        Assert.Equal(4, store.Replaced.Count); // los otros sí
    }

    [Fact]
    public async Task RefreshAll_SanityMinRows_RechazaYConservaPrevios()
    {
        var store = new FakeFinOpsDataStore();
        // minRowsOverride=10: los CSVs de 1-3 filas no pasan el sanity
        var svc = Build(store, HappyCsvs(eligRows: 3), minRowsOverride: 10);
        var results = await svc.RefreshAllAsync(CancellationToken.None);

        Assert.All(results, r => Assert.StartsWith("error:", r.Status));
        Assert.Empty(store.Replaced);
    }

    [Fact]
    public async Task RefreshAll_HeaderInesperado_Rechaza()
    {
        var csvs = HappyCsvs();
        csvs["PricingUnits.csv"] = "\"OtraCosa\",\"X\"\n\"1\",\"2\"";
        var store = new FakeFinOpsDataStore();
        var svc = Build(store, csvs);
        var results = await svc.RefreshAllAsync(CancellationToken.None);

        Assert.StartsWith("error:", results.Single(r => r.Dataset == "pricing_units").Status);
        Assert.False(store.Replaced.ContainsKey("pricing_units"));
    }

    [Fact]
    public async Task EnsureFresh_ConDatosFrescos_NoDescarga()
    {
        var store = new FakeFinOpsDataStore
        {
            Status = FinOpsDatasets.All
                .Select(d => new FinOpsRefreshStatus(d.Key, DateTime.UtcNow.AddDays(-1), 100, "ok")).ToList(),
        };
        var svc = Build(store, new Dictionary<string, string>()); // sin CSVs: si descargara, fallaría
        var fresh = await svc.EnsureFreshBestEffortAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(fresh);
        Assert.Empty(store.Replaced);
    }

    [Fact]
    public async Task RefreshAll_HeaderInesperadoGigante_TruncaDetalleAntesDeRegistrar()
    {
        var csvs = HappyCsvs();
        // Header absurdamente largo (columna de 2000 chars): sin truncar, "error:{detail}" desborda
        // status NVARCHAR(400) y RecordRefreshAsync lanzaría (silenciado -> sin fila de log).
        csvs["PricingUnits.csv"] = $"\"{new string('X', 2000)}\"\n\"1\"";
        var store = new FakeFinOpsDataStore();
        var svc = Build(store, csvs);
        var results = await svc.RefreshAllAsync(CancellationToken.None);

        var pricingUnits = results.Single(r => r.Dataset == "pricing_units");
        Assert.StartsWith("error:", pricingUnits.Status);

        var logEntry = Assert.Single(store.Log, l => l.Dataset == "pricing_units");
        Assert.True(logEntry.Status.Length <= 310, $"Status length {logEntry.Status.Length} > 310");
    }

    [Fact]
    public async Task EnsureFresh_EligibilityRancio_RefrescaSoloLosRancios()
    {
        var store = new FakeFinOpsDataStore
        {
            Status = FinOpsDatasets.All.Select(d => new FinOpsRefreshStatus(
                d.Key,
                d.Key == "commitment_eligibility" ? DateTime.UtcNow.AddDays(-9) : DateTime.UtcNow.AddDays(-1),
                100, "ok")).ToList(),
        };
        var svc = Build(store, HappyCsvs());
        await svc.EnsureFreshBestEffortAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(store.Replaced.ContainsKey("commitment_eligibility"));
        Assert.False(store.Replaced.ContainsKey("regions"));
    }
}
