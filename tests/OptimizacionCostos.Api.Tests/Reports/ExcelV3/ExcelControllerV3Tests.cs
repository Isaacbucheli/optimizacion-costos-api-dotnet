using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Reports;
using OptimizacionCostos.Api.Features.Reports.ExcelV3;
using OptimizacionCostos.Api.Features.Storage;
using OptimizacionCostos.Api.Tests.CostEngine.Api;

namespace OptimizacionCostos.Api.Tests.Reports.ExcelV3;

/// <summary>Fake de ICostExcelExporter (motor "template", camino viejo): registra la última llamada.</summary>
public sealed class FakeCostExcelExporter : ICostExcelExporter
{
    public int? LastAnalysisId { get; private set; }
    public byte[] BytesToReturn { get; set; } = [1, 2, 3];

    public Task<byte[]> GenerateAsync(int analysisId, CancellationToken ct)
    {
        LastAnalysisId = analysisId;
        return Task.FromResult(BytesToReturn);
    }
}

/// <summary>Fake de ICostExcelExporterV3 (motor "code"): registra el margen recibido.</summary>
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
/// Levanta la API real en memoria (patrón UserSessionsApiTestFactory) reemplazando exporters,
/// blob storage, acceso y el store de archivos por fakes. El engine ("code"/"template") se fija
/// por variable de entorno EXCEL_EXPORT_ENGINE antes de construir el host (AppConfig la lee una
/// sola vez al arrancar).
/// </summary>
public sealed class ExcelV3ApiTestFactory : WebApplicationFactory<Program>
{
    public const string Secret = "test-secret-con-mas-de-32-caracteres-1234567890";

    public FakeUserDirectory Directory { get; } = new();
    public FakeAnalysisAccess Access { get; } = new();
    public FakeCostExcelExporter ExporterTemplate { get; } = new();
    public FakeCostExcelExporterV3 ExporterV3 { get; } = new();
    public FakeBlobStorageService Blobs { get; } = new();
    public FakeAnalysisFileStore Files { get; } = new();

    public ExcelV3ApiTestFactory(string engine)
    {
        Environment.SetEnvironmentVariable("JWT_SECRET", Secret);
        Environment.SetEnvironmentVariable("EXCEL_EXPORT_ENGINE", engine);
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
            services.RemoveAll<ICostExcelExporter>();
            services.AddSingleton<ICostExcelExporter>(ExporterTemplate);
            services.RemoveAll<ICostExcelExporterV3>();
            services.AddSingleton<ICostExcelExporterV3>(ExporterV3);
            services.RemoveAll<IBlobStorageService>();
            services.AddSingleton<IBlobStorageService>(Blobs);
            services.RemoveAll<IAnalysisFileStore>();
            services.AddSingleton<IAnalysisFileStore>(Files);
            services.RemoveAll<ICostExcelDataSourceV3>();
            services.AddSingleton<ICostExcelDataSourceV3>(new FakeCostExcelDataSourceV3ForController());
        });
    }
}

// Serializar: las factories manipulan variables de entorno de proceso (igual que UserSessionsEnv).
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
    public async Task Engine_code_sin_body_devuelve_200_con_nombre_v3_y_margen_null()
    {
        using var f = new ExcelV3ApiTestFactory("code");
        var client = AdminClient(f);

        var res = await client.PostAsync("/excel/generate/1", content: null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Matches(@"^Optimizacion-Costos-.+\.xlsx$", body.GetProperty("file_name").GetString());
        Assert.Null(f.ExporterV3.LastMarginPct);
        Assert.Equal(1, f.ExporterV3.LastAnalysisId);
    }

    [Fact]
    public async Task Engine_code_con_margen_10_pasa_el_margen_al_exportador()
    {
        using var f = new ExcelV3ApiTestFactory("code");
        var client = AdminClient(f);

        var res = await client.PostAsJsonAsync("/excel/generate/1", new { margin_pct = 10 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(10m, f.ExporterV3.LastMarginPct);
    }

    [Fact]
    public async Task Engine_code_con_margen_150_devuelve_400()
    {
        using var f = new ExcelV3ApiTestFactory("code");
        var client = AdminClient(f);

        var res = await client.PostAsJsonAsync("/excel/generate/1", new { margin_pct = 150 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Engine_template_con_margen_devuelve_400_mencionando_excel_export_engine()
    {
        using var f = new ExcelV3ApiTestFactory("template");
        var client = AdminClient(f);

        var res = await client.PostAsJsonAsync("/excel/generate/1", new { margin_pct = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("EXCEL_EXPORT_ENGINE", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Engine_template_sin_body_devuelve_200_via_exportador_viejo()
    {
        using var f = new ExcelV3ApiTestFactory("template");
        var client = AdminClient(f);

        var res = await client.PostAsync("/excel/generate/1", content: null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("resultado-optimizacion-costos.xlsx", body.GetProperty("file_name").GetString());
        Assert.Equal("Excel generado correctamente desde la plantilla", body.GetProperty("message").GetString());
        Assert.Equal(1, f.ExporterTemplate.LastAnalysisId);
        Assert.Null(f.ExporterV3.LastAnalysisId); // el motor v3 no se invoca en este camino
    }
}
