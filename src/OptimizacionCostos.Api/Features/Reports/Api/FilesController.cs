using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Storage;

namespace OptimizacionCostos.Api.Features.Reports.Api;

/// <summary>
/// Carga de archivos de inventario para un análisis. Port de app/routes/files.py (prefix /files).
/// </summary>
[ApiController]
[Authorize]
[Route("files")]
[RequireModule(Modules.Report, ModuleAccess.Edit)]
public sealed class FilesController(
    IBlobStorageService blobs,
    IAnalysisAccess access,
    ISqlConnectionFactory factory,
    AppConfig config,
    ILogger<FilesController> logger) : ControllerBase
{
    [HttpPost("upload/{analysisId:int}")]
    public async Task<IActionResult> UploadAnalysisFile(int analysisId, IFormFile? file, CancellationToken ct)
    {
        try
        {
            var chk = await access.AssertAnalysisAccessAsync(User, analysisId, ct);
            if (!chk.Ok) return Translate(chk);

            if (file is null) return BadRequest(new { detail = "Falta el archivo a cargar." });
            var fileName = UploadValidation.SafeUploadFilename(file.FileName);

            byte[] content;
            await using (var stream = file.OpenReadStream())
                content = await UploadValidation.ReadLimitedUploadAsync(stream, ct: ct);

            var blobName = $"analysis-{analysisId}/{Guid.NewGuid()}-{fileName}";
            await blobs.UploadAsync(config.StorageContainerUploads, blobName, content, file.ContentType, ct);

            var fileId = await InsertFileAsync(
                analysisId, "inventory", fileName, config.StorageContainerUploads, blobName,
                file.ContentType, content.Length, ct);

            return Ok(new
            {
                message = "Archivo cargado correctamente",
                file_id = fileId,
                file_name = fileName,
                container = config.StorageContainerUploads,
                blob_name = blobName,
            });
        }
        catch (UploadValidationException ex)
        {
            return StatusCode(ex.StatusCode, new { detail = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error interno no controlado cargando archivo analysis={Aid}", analysisId);
            return StatusCode(500, new { detail = "Error interno del servidor" });
        }
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
