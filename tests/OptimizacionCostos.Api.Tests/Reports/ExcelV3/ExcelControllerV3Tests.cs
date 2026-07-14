using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.Reports;
using OptimizacionCostos.Api.Features.Reports.ExcelV3;
using OptimizacionCostos.Api.Features.Storage;
using OptimizacionCostos.Api.Tests.CostEngine.Api;

namespace OptimizacionCostos.Api.Tests.Reports.ExcelV3;

/// <summary>Fake de ICostExcelExporterV3 (único motor de Excel): registra el margen recibido.</summary>
public sealed class FakeCostExcelExporterV3 : ICostExcelExporterV3
{
    public decimal? LastMarginPct { get; private set; }
    public int? LastAnalysisId { get; private set; }
    public ExcelV3Result ResultToReturn { get; set; } = new([1, 2, 3], "Optimizacion-Costos-Cliente-Prueba-20260707.xlsx");

    public Task<ExcelV3Result> GenerateAsync(int analysisId, decimal? marginPct, CancellationToken ct)
    {
        LastAnalysisId = analysisId;
        LastMarginPct = marginPct;
        return Task.FromResult(ResultToReturn);
    }
}

/// <summary>Fake de IBlobStorageService: no sube nada de verdad, solo registra la llamada.</summary>
public sealed class FakeBlobStorageService : IBlobStorageService
{
    public List<(string Container, string BlobName)> Uploads { get; } = [];

    public Task UploadAsync(string containerName, string blobName, byte[] data, string? contentType = null, CancellationToken ct = default)
    {
        Uploads.Add((containerName, blobName));
        return Task.CompletedTask;
    }

    public Task<byte[]> DownloadAsync(string containerName, string blobName, CancellationToken ct = default) =>
        Task.FromResult<byte[]>([1, 2, 3]);

    public Task DeleteAsync(string containerName, string blobName, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Fake de IAnalysisFileStore: siempre inserta con id fijo, sin BD.</summary>
public sealed class FakeAnalysisFileStore : IAnalysisFileStore
{
    public int NextFileId { get; set; } = 1;

    public Task<int> InsertFileAsync(int analysisId, string fileType, string originalName, string container, string blobName,
        string? contentType, long sizeBytes, CancellationToken ct) => Task.FromResult(NextFileId);

    public Task<GeneratedFileRecord?> GetGeneratedFileAsync(int fileId, CancellationToken ct) =>
        Task.FromResult<GeneratedFileRecord?>(null);
}

/// <summary>Fake de ICostExcelDataSourceV3 (por si el DI lo pide para construir el pipeline).</summary>
public sealed class FakeCostExcelDataSourceV3ForController : ICostExcelDataSourceV3
{
    public Task<ExcelV3Data> LoadAsync(int analysisId, CancellationToken ct) => Task.FromResult(FakeDataSourceV3.Default());
}

/// <summary>
/// Levanta la API real en memoria (patrón UserSessionsApiTestFactory) reemplazando el exportador v3,
/// blob storage, acceso y el store de archivos por fakes. El Excel se genera SIEMPRE con el motor v3
/// (código); el motor "template" y su plantilla se eliminaron (limpieza 2026-07-07).
/// </summary>
public sealed class ExcelV3ApiTestFactory : WebApplicationFactory<Program>
{
    public const string Secret = "test-secret-con-mas-de-32-caracteres-1234567890";

    public FakeUserDirectory Directory { get; } = new();
    public FakeAnalysisAccess Access { get; } = new();
    public FakeCostExcelExporterV3 ExporterV3 { get; } = new();
    public FakeBlobStorageService Blobs { get; } = new();
    public FakeAnalysisFileStore Files { get; } = new();

    public ExcelV3ApiTestFactory()
    {
        Environment.SetEnvironmentVariable("JWT_SECRET", Secret);
        Directory.Add("admin@bit.ec", Roles.Admin);
        Access.AnalysisToClient[1] = 1; // admin: acceso global, no requiere asignación.
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserDirectory>();
            services.AddSingleton<IUserDirectory>(Directory);
            services.RemoveAll<IAnalysisAccess>();
            services.AddSingleton<IAnalysisAccess>(Access);
            services.RemoveAll<ICostExcelExporterV3>();
            services.AddSingleton<ICostExcelExporterV3>(ExporterV3);
            services.RemoveAll<IBlobStorageService>();
            services.AddSingleton<IBlobStorageService>(Blobs);
            services.RemoveAll<IAnalysisFileStore>();
            services.AddSingleton<IAnalysisFileStore>(Files);
            services.RemoveAll<ICostExcelDataSourceV3>();
            services.AddSingleton<ICostExcelDataSourceV3>(new FakeCostExcelDataSourceV3ForController());
            services.RemoveAll<IModulePermissionStore>();
            services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
        });
    }
}

// Serializar: la factory manipula JWT_SECRET (variable de entorno de proceso, igual que UserSessionsEnv).
[Collection("UserSessionsEnv")]
public class ExcelControllerV3Tests
{
    private static HttpClient AdminClient(ExcelV3ApiTestFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BitJwt.Create(ExcelV3ApiTestFactory.Secret, "admin@bit.ec", "Admin", Roles.Admin));
        return c;
    }

    [Fact]
    public async Task Sin_body_devuelve_200_con_nombre_v3_y_margen_null()
    {
        using var f = new ExcelV3ApiTestFactory();
        var client = AdminClient(f);

        var res = await client.PostAsync("/excel/generate/1", content: null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Matches(@"^Optimizacion-Costos-.+\.xlsx$", body.GetProperty("file_name").GetString());
        Assert.Null(f.ExporterV3.LastMarginPct);
        Assert.Equal(1, f.ExporterV3.LastAnalysisId);
    }

    [Fact]
    public async Task Con_margen_10_pasa_el_margen_al_exportador()
    {
        using var f = new ExcelV3ApiTestFactory();
        var client = AdminClient(f);

        var res = await client.PostAsJsonAsync("/excel/generate/1", new { margin_pct = 10 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(10m, f.ExporterV3.LastMarginPct);
    }

    [Fact]
    public async Task Con_margen_150_devuelve_400()
    {
        using var f = new ExcelV3ApiTestFactory();
        var client = AdminClient(f);

        var res = await client.PostAsJsonAsync("/excel/generate/1", new { margin_pct = 150 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
