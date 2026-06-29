using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Features.Storage;

/// <summary>
/// Servicio de Azure Blob Storage. Port de app/storage.py (upload/download/delete) usando
/// Azure.Storage.Blobs + DefaultAzureCredential. Lo usan clientes (logos), informes y export
/// Excel. La identidad necesita "Storage Blob Data Contributor".
/// </summary>
public interface IBlobStorageService
{
    Task UploadAsync(string containerName, string blobName, byte[] data, string? contentType = null, CancellationToken ct = default);
    Task<byte[]> DownloadAsync(string containerName, string blobName, CancellationToken ct = default);
    Task DeleteAsync(string containerName, string blobName, CancellationToken ct = default);
}

public sealed class BlobStorageService(AppConfig config) : IBlobStorageService
{
    private readonly Lazy<BlobServiceClient> _client = new(() =>
        new BlobServiceClient(
            new Uri($"https://{config.StorageAccountName}.blob.core.windows.net"),
            new DefaultAzureCredential()));

    private BlobClient Blob(string container, string blob) =>
        _client.Value.GetBlobContainerClient(container).GetBlobClient(blob);

    public async Task UploadAsync(string containerName, string blobName, byte[] data, string? contentType = null, CancellationToken ct = default)
    {
        var blob = Blob(containerName, blobName);
        using var stream = new MemoryStream(data, writable: false);
        var options = new BlobUploadOptions();
        if (!string.IsNullOrEmpty(contentType))
            options.HttpHeaders = new BlobHttpHeaders { ContentType = contentType };
        await blob.UploadAsync(stream, options, ct);
    }

    public async Task<byte[]> DownloadAsync(string containerName, string blobName, CancellationToken ct = default)
    {
        var blob = Blob(containerName, blobName);
        var resp = await blob.DownloadContentAsync(ct);
        return resp.Value.Content.ToArray();
    }

    public async Task DeleteAsync(string containerName, string blobName, CancellationToken ct = default)
    {
        // Best-effort: no falla si el blob no existe (paridad con delete_blob).
        try
        {
            await Blob(containerName, blobName).DeleteIfExistsAsync(cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // ignorar
        }
    }
}
