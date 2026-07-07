using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>Fila mínima de dbo.analysis_files necesaria para servir la descarga de un Excel generado.</summary>
public sealed record GeneratedFileRecord(string? OriginalFileName, string StorageContainer, string StorageBlobName, string? ContentType);

/// <summary>
/// Persistencia de dbo.analysis_files para el módulo de Excel de costos (ExcelController). Extraído
/// de ExcelController (que antes ejecutaba SQL directo con ISqlConnectionFactory) para poder testear
/// el controller con WebApplicationFactory + fakes, sin depender de una base de datos real. Mismas
/// queries, byte-idénticas a las que tenía el controller.
/// </summary>
public interface IAnalysisFileStore
{
    Task<int> InsertFileAsync(
        int analysisId, string fileType, string originalName, string container, string blobName,
        string? contentType, long sizeBytes, CancellationToken ct);

    /// <summary>null si no existe un archivo generado ("output") con ese file_id.</summary>
    Task<GeneratedFileRecord?> GetGeneratedFileAsync(int fileId, CancellationToken ct);
}

public sealed class SqlAnalysisFileStore(ISqlConnectionFactory factory) : IAnalysisFileStore
{
    public async Task<int> InsertFileAsync(
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

    public async Task<GeneratedFileRecord?> GetGeneratedFileAsync(int fileId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT original_file_name, storage_container, storage_blob_name, content_type
            FROM dbo.analysis_files
            WHERE file_id = @id AND file_type = 'output'
            """;
        cmd.Parameters.Add(new SqlParameter("@id", fileId));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var originalName = r.IsDBNull(0) ? null : r.GetString(0);
        var container = r.GetString(1);
        var blobName = r.GetString(2);
        var contentType = r.IsDBNull(3) ? null : r.GetString(3);
        return new GeneratedFileRecord(originalName, container, blobName, contentType);
    }
}
