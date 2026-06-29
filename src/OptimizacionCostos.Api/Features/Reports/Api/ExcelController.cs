using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Storage;

namespace OptimizacionCostos.Api.Features.Reports.Api;

/// <summary>
/// Generación/descarga del Excel de COSTOS de un análisis. Port de app/routes/excel.py (prefix /excel).
/// El exporter lee cost_results + azure_resources (NO recalcula) y rellena la plantilla del Blob.
/// </summary>
[ApiController]
[Authorize]
[Route("excel")]
public sealed class ExcelController(
    ICostExcelExporter exporter,
    IBlobStorageService blobs,
    IAnalysisAccess access,
    ISqlConnectionFactory factory,
    AppConfig config,
    ILogger<ExcelController> logger) : ControllerBase
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [HttpPost("generate/{analysisId:int}")]
    public async Task<IActionResult> GenerateFromTemplate(int analysisId, CancellationToken ct)
    {
        var chk = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
        if (!chk.Ok) return Translate(chk);

        try
        {
            var excelBytes = await exporter.GenerateAsync(analysisId, ct);
            var blobName = $"analysis-{analysisId}/resultado-optimizacion-costos.xlsx";
            await blobs.UploadAsync(config.StorageContainerOutputs, blobName, excelBytes, XlsxContentType, ct);

            var fileId = await InsertFileAsync(
                analysisId, "output", "resultado-optimizacion-costos.xlsx",
                config.StorageContainerOutputs, blobName, XlsxContentType, excelBytes.Length, ct);

            return Ok(new
            {
                message = "Excel generado correctamente desde la plantilla",
                file_id = fileId,
                container = config.StorageContainerOutputs,
                blob_name = blobName,
                file_name = "resultado-optimizacion-costos.xlsx",
                download_url = $"/excel/files/{fileId}/download",
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error interno no controlado generando Excel analysis={Aid}", analysisId);
            return StatusCode(500, new { detail = "Error interno del servidor" });
        }
    }

    [HttpGet("files/{fileId:int}/download")]
    public async Task<IActionResult> DownloadGenerated(int fileId, CancellationToken ct)
    {
        var chk = await access.AssertFileAccessAsync(User, fileId, ct);
        if (!chk.Ok) return Translate(chk);

        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT original_file_name, storage_container, storage_blob_name, content_type
            FROM dbo.analysis_files
            WHERE file_id = @id AND file_type = 'output'
            """;
        cmd.Parameters.Add(new SqlParameter("@id", fileId));

        string container, blobName;
        string? originalName, contentType;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return NotFound(new { detail = "Generated Excel not found" });
            originalName = r.IsDBNull(0) ? null : r.GetString(0);
            container = r.GetString(1);
            blobName = r.GetString(2);
            contentType = r.IsDBNull(3) ? null : r.GetString(3);
        }

        var data = await blobs.DownloadAsync(container, blobName, ct);
        var filename = string.IsNullOrEmpty(originalName) ? "resultado-optimizacion-costos.xlsx" : originalName;
        return File(data, contentType ?? XlsxContentType, filename);
    }

    private async Task<int> InsertFileAsync(
        int analysisId, string fileType, string originalName, string container, string blobName,
        string? contentType, long sizeBytes, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dbo.analysis_files (
                analysis_id, file_type, original_file_name, storage_container,
                storage_blob_name, content_type, file_size_bytes)
            OUTPUT INSERTED.file_id
            VALUES (@a, @t, @n, @c, @b, @ct, @sz)
            """;
        cmd.Parameters.Add(new SqlParameter("@a", analysisId));
        cmd.Parameters.Add(new SqlParameter("@t", fileType));
        cmd.Parameters.Add(new SqlParameter("@n", originalName));
        cmd.Parameters.Add(new SqlParameter("@c", container));
        cmd.Parameters.Add(new SqlParameter("@b", blobName));
        cmd.Parameters.Add(new SqlParameter("@ct", (object?)contentType ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@sz", sizeBytes));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso" }),
        _ => Ok(),
    };
}
