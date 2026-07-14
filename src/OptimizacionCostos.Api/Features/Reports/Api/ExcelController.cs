using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Reports.ExcelV3;
using OptimizacionCostos.Api.Features.Storage;

namespace OptimizacionCostos.Api.Features.Reports.Api;

/// <summary>Body opcional de POST /excel/generate/{analysisId}: margen comercial (%), solo válido con engine "code".</summary>
public sealed record ExcelGenerateRequest(decimal? MarginPct);

/// <summary>
/// Generación/descarga del Excel de COSTOS de un análisis. Port de app/routes/excel.py (prefix /excel).
/// Motor seleccionable por AppConfig.ExcelExportEngine (EXCEL_EXPORT_ENGINE):
///   - "template" (default, actual): rellena la plantilla del Blob (ICostExcelExporter). Camino
///     intacto, sin margen.
///   - "code": arma el workbook 100% desde código (ICostExcelExporterV3), con margen opcional.
/// Ninguno de los dos recalcula costos: ambos leen cost_results + azure_resources ya calculados.
/// </summary>
[ApiController]
[Authorize]
[Route("excel")]
[RequireModule(Modules.Costos)]
public sealed class ExcelController(
    ICostExcelExporterV3 exporterV3,
    IBlobStorageService blobs,
    IAnalysisAccess access,
    IAnalysisFileStore files,
    AppConfig config,
    ILogger<ExcelController> logger) : ControllerBase
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [HttpPost("generate/{analysisId:int}")]
    public async Task<IActionResult> Generate(int analysisId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ExcelGenerateRequest? payload,
        CancellationToken ct)
    {
        var chk = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!chk.Ok) return Translate(chk);

        var marginPct = payload?.MarginPct;
        if (marginPct is { } m && !(m > 0 && m <= 100))
            return BadRequest(new { detail = "El margen debe ser mayor a 0 y menor o igual a 100." });

        try
        {
            return await GenerateWithCodeEngine(analysisId, marginPct, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error interno no controlado generando Excel analysis={Aid}", analysisId);
            return StatusCode(500, new { detail = "Error interno del servidor" });
        }
    }

    /// <summary>Genera el workbook v3 (armado 100% desde código, margen opcional) y lo sube al Blob de salidas.</summary>
    private async Task<IActionResult> GenerateWithCodeEngine(int analysisId, decimal? marginPct, CancellationToken ct)
    {
        var result = await exporterV3.GenerateAsync(analysisId, marginPct, ct);
        var blobName = $"analysis-{analysisId}/{result.FileName}";
        await blobs.UploadAsync(config.StorageContainerOutputs, blobName, result.Bytes, XlsxContentType, ct);

        var fileId = await files.InsertFileAsync(
            analysisId, "output", result.FileName,
            config.StorageContainerOutputs, blobName, XlsxContentType, result.Bytes.Length, ct);

        return Ok(new
        {
            message = "Excel generado correctamente",
            file_id = fileId,
            container = config.StorageContainerOutputs,
            blob_name = blobName,
            file_name = result.FileName,
            download_url = $"/excel/files/{fileId}/download",
        });
    }

    [HttpGet("files/{fileId:int}/download")]
    public async Task<IActionResult> DownloadGenerated(int fileId, CancellationToken ct)
    {
        var chk = await access.AssertFileAccessAsync(User, fileId, ct);
        if (!chk.Ok) return Translate(chk);

        var record = await files.GetGeneratedFileAsync(fileId, ct);
        if (record is null) return NotFound(new { detail = "Generated Excel not found" });

        var data = await blobs.DownloadAsync(record.StorageContainer, record.StorageBlobName, ct);
        var filename = string.IsNullOrEmpty(record.OriginalFileName) ? "resultado-optimizacion-costos.xlsx" : record.OriginalFileName;
        return File(data, record.ContentType ?? XlsxContentType, filename);
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso" }),
        _ => Ok(),
    };
}
