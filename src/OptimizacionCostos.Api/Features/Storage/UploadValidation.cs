namespace OptimizacionCostos.Api.Features.Storage;

/// <summary>
/// Error de validación de carga de archivos. El controller lo traduce al status HTTP indicado
/// (paridad con las HTTPException 400/413 de app/upload_validation.py).
/// </summary>
public sealed class UploadValidationException(int statusCode, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Validación de cargas. Port de app/upload_validation.py (safe_upload_filename + read_limited_upload).
/// </summary>
public static class UploadValidation
{
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    private const int ReadChunkBytes = 1024 * 1024;

    /// <summary>Port de safe_upload_filename: solo el nombre base, sin ruta; 400 si queda vacío.</summary>
    public static string SafeUploadFilename(string? filename)
    {
        // Path.GetFileName en Windows separa por '\' y '/'; PurePath().name (Linux) solo por '/'.
        // Tomar el último segmento por ambos separadores es equivalente y más defensivo.
        var raw = filename ?? "";
        var idx = raw.LastIndexOfAny(['/', '\\']);
        var normalized = (idx >= 0 ? raw[(idx + 1)..] : raw).Trim();
        if (string.IsNullOrEmpty(normalized))
            throw new UploadValidationException(400, "El archivo debe tener un nombre valido.");
        return normalized;
    }

    /// <summary>
    /// Port de read_limited_upload: lee por chunks y aborta con 413 si supera el límite (10 MB).
    /// </summary>
    public static async Task<byte[]> ReadLimitedUploadAsync(
        Stream stream, long maxBytes = MaxUploadBytes, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkBytes];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
                throw new UploadValidationException(
                    413, $"El archivo supera el limite permitido de {maxBytes / (1024 * 1024)} MB.");
        }
        return buffer.ToArray();
    }
}
