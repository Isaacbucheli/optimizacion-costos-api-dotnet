using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.Optimization;
using OptimizacionCostos.Api.Tests.CostEngine.Api;

namespace OptimizacionCostos.Api.Tests.Optimization;

/// <summary>Fake en memoria de IOptimizationService (solo lo que usa el export).</summary>
public sealed class FakeOptimizationService : IOptimizationService
{
    public bool Allowed { get; set; } = true;
    public Dictionary<int, int> ScanToClient { get; } = new();
    public List<IReadOnlyDictionary<string, object?>> Scans { get; } = [];
    public List<IReadOnlyDictionary<string, object?>> Findings { get; } = [];

    public bool AccessAllowed(string? email) => Allowed;
    public Task<int?> ScanOwnerAsync(int scanId, CancellationToken ct = default) =>
        Task.FromResult<int?>(ScanToClient.TryGetValue(scanId, out var c) ? c : null);
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListScansAsync(int clientId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Scans);
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ScanFindingsAsync(int scanId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Findings);
    public Task<IReadOnlyDictionary<string, object?>> RunScanAsync(int clientId, string? actor, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<int?> FindingStateOwnerAsync(byte[] fingerprint, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);
    public Task<bool> UpdateStateAsync(byte[] fingerprint, string state, string? notes, string? actor, CancellationToken ct = default) =>
        Task.FromResult(false);
    public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Fake de IClientStore: solo GetNameAsync; el resto no se usa en el export.</summary>
public sealed class FakeClientStoreForExcel : IClientStore
{
    public Dictionary<int, string> Names { get; } = new();

    public Task<string?> GetNameAsync(int clientId, CancellationToken ct = default) =>
        Task.FromResult(Names.TryGetValue(clientId, out var n) ? n : null);

    public Task<IReadOnlyList<ClientListItem>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<int> CreateAsync(string clientName, string? taxId, string? contactName, string? contactEmail, string? notes, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> ExistsAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> NameExistsAsync(string name, int excludeClientId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> RenameAsync(int clientId, string name, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<(string Name, string? LogoBlobName)?> GetNameAndLogoAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, int>> PurgeDataAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<(IReadOnlyDictionary<string, int> Counts, string? LogoBlobName)> DeleteClientCascadeAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task UpdateLogoMetaAsync(int clientId, string blobName, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<(string? BlobName, string? ContentType)?> GetLogoAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ClearLogoAsync(int clientId, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Levanta la API real en memoria (patrón TestAppFactory) con fakes de optimización.</summary>
public sealed class OptimizationExcelApiTestFactory : WebApplicationFactory<Program>
{
    public const string Secret = "test-secret-con-mas-de-32-caracteres-1234567890";

    public FakeUserDirectory Directory { get; } = new();
    public FakeAnalysisAccess Access { get; } = new();
    public FakeOptimizationService Svc { get; } = new();
    public FakeClientStoreForExcel Clients { get; } = new();

    public OptimizationExcelApiTestFactory()
    {
        Environment.SetEnvironmentVariable("JWT_SECRET", Secret);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserDirectory>(); services.AddSingleton<IUserDirectory>(Directory);
            services.RemoveAll<IAnalysisAccess>(); services.AddSingleton<IAnalysisAccess>(Access);
            services.RemoveAll<IOptimizationService>(); services.AddSingleton<IOptimizationService>(Svc);
            services.RemoveAll<IClientStore>(); services.AddSingleton<IClientStore>(Clients);
            services.RemoveAll<IModulePermissionStore>();
            services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
        });
    }
}

public class OptimizationExcelEndpointTests : IClassFixture<OptimizationExcelApiTestFactory>
{
    private readonly OptimizationExcelApiTestFactory _factory;

    public OptimizationExcelEndpointTests(OptimizationExcelApiTestFactory factory)
    {
        _factory = factory;
        _factory.Directory.Add("admin@test.com", "admin");
        _factory.Svc.Allowed = true;
        _factory.Svc.ScanToClient[7] = 6;
        _factory.Svc.Scans.Clear();
        _factory.Svc.Scans.Add(new Dictionary<string, object?>
        {
            ["scan_id"] = 7, ["started_at"] = "2026-07-03T14:00:00", ["finished_at"] = "2026-07-03T14:02:00",
            ["status"] = "completed", ["subscriptions_scanned"] = 12, ["findings_count"] = 2,
            ["total_estimated_monthly_savings"] = 250d, ["currency"] = "USD",
        });
        _factory.Svc.Findings.Clear();
        _factory.Svc.Findings.Add(Finding("vms_without_ahb", "vm-1", 200, "abierto"));
        _factory.Svc.Findings.Add(Finding("orphaned_disks", "disk-1", 50, "resuelto"));
        _factory.Clients.Names[6] = "Banco Delta";
    }

    private static IReadOnlyDictionary<string, object?> Finding(string checkId, string name, double savings, string state) =>
        new Dictionary<string, object?>
        {
            ["check_id"] = checkId, ["category"] = "cost_waste", ["severity"] = "medium",
            ["subscription_id"] = "sub-1", ["azure_resource_id"] = $"/subscriptions/s/resourceGroups/rg/providers/x/y/{name}",
            ["resource_name"] = name, ["resource_type"] = "t", ["region"] = "eastus2",
            ["details"] = JsonSerializer.Deserialize<JsonElement>("{}"),
            ["estimated_monthly_savings"] = savings, ["currency"] = "USD",
            ["fingerprint"] = "ab", ["state"] = state, ["notes"] = null,
        };

    private HttpClient Client(string role = "admin")
    {
        var client = _factory.CreateClient();
        var token = BitJwt.Create(OptimizationExcelApiTestFactory.Secret, "admin@test.com", "Admin", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Descarga_xlsx_con_content_type_y_nombre()
    {
        var res = await Client().GetAsync("/optimization/scans/7/excel");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("oportunidades-optimizacion-Banco-Delta-20260703.xlsx", res.Content.Headers.ContentDisposition!.FileNameStar);
        using var wb = new XLWorkbook(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
        Assert.Equal(new[] { "Resumen", "Azure Hybrid Benefit", "Storage" }, wb.Worksheets.Select(w => w.Name).ToList());
    }

    [Fact]
    public async Task Filtra_por_states()
    {
        var res = await Client().GetAsync("/optimization/scans/7/excel?states=abierto");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var wb = new XLWorkbook(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
        Assert.Equal(new[] { "Resumen", "Azure Hybrid Benefit" }, wb.Worksheets.Select(w => w.Name).ToList()); // el resuelto (Storage) queda fuera
    }

    [Fact]
    public async Task States_invalido_400()
    {
        var res = await Client().GetAsync("/optimization/scans/7/excel?states=abierto,noexiste");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Scan_inexistente_404()
    {
        var res = await Client().GetAsync("/optimization/scans/999/excel");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Sin_allowlist_403()
    {
        _factory.Svc.Allowed = false;
        try
        {
            var res = await Client().GetAsync("/optimization/scans/7/excel");
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        finally { _factory.Svc.Allowed = true; }
    }
}
