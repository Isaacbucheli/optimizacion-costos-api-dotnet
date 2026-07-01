using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Storage;

namespace OptimizacionCostos.Api.Features.Clients.Api;

/// <summary>
/// Gestión de clientes. Port de app/routes/clients.py (prefix /clients): CRUD, logo (PNG/JPG/SVG
/// en Blob, máx 2 MB), purga e eliminación en cascada (admin, con confirmación de nombre).
/// </summary>
[ApiController]
[Authorize]
[Route("clients")]
public sealed class ClientsController(
    IClientStore store,
    IBlobStorageService blobs,
    IAnalysisAccess access,
    AppConfig config,
    ILogger<ClientsController> logger) : ControllerBase
{
    private const long LogoMaxBytes = 2 * 1024 * 1024;
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public sealed record ClientCreateRequest(string? ClientName, string? TaxId, string? ContactName, string? ContactEmail, string? Notes);
    public sealed record ClientUpdateRequest(string? ClientName);
    public sealed record ConfirmNameRequest(string? ConfirmClientName);

    private static string LogoBlobName(int clientId) => $"client-logos/client-{clientId}";

    // -------------------- GET /clients --------------------
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var allowed = await access.AccessibleClientIdsAsync(User, ct); // null = todos (admin)
        var all = await store.ListAsync(ct);
        var visible = allowed is null ? all : all.Where(c => allowed.Contains(c.ClientId));
        return Ok(visible);
    }

    // -------------------- POST /clients (admin) --------------------
    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Create([FromBody] ClientCreateRequest payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload.ClientName))
            return BadRequest(new { detail = "client_name es requerido" });
        var id = await store.CreateAsync(payload.ClientName!, payload.TaxId, payload.ContactName, payload.ContactEmail, payload.Notes, ct);
        return Ok(new { message = "Cliente creado correctamente", client_id = id });
    }

    // -------------------- PUT /clients/{id} (admin) --------------------
    [HttpPut("{clientId:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Update(int clientId, [FromBody] ClientUpdateRequest payload, CancellationToken ct)
    {
        var name = payload.ClientName?.Trim();
        if (string.IsNullOrEmpty(name))
            return BadRequest(new { detail = "El nombre del cliente es requerido" });
        if (!await store.ExistsAsync(clientId, ct))
            return NotFound(new { detail = "Cliente no encontrado" });
        if (await store.NameExistsAsync(name, clientId, ct))
            return Conflict(new { detail = "Ya existe un cliente con ese nombre" });
        await store.RenameAsync(clientId, name, ct);
        return Ok(new { client_id = clientId, client_name = name });
    }

    // -------------------- POST /clients/{id}/purge (admin) --------------------
    [HttpPost("{clientId:int}/purge")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Purge(int clientId, [FromBody] ConfirmNameRequest payload, CancellationToken ct)
    {
        var name = await store.GetNameAsync(clientId, ct);
        if (name is null) return NotFound(new { detail = "Client not found" });
        if ((payload.ConfirmClientName ?? "").Trim() != name.Trim())
            return BadRequest(new { detail = "El nombre de confirmacion no coincide con el del cliente." });

        var deleted = await store.PurgeDataAsync(clientId, ct);
        var total = deleted.Values.Sum();
        logger.LogWarning("Client purge client_id={Id} by={By} total_rows={Total}", clientId, User.FindFirst("sub")?.Value, total);
        return Ok(new { message = "Datos del cliente purgados correctamente", client_id = clientId, client_name = name, deleted, total_rows = total });
    }

    // -------------------- DELETE /clients/{id} (admin) --------------------
    [HttpDelete("{clientId:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(int clientId, [FromBody] ConfirmNameRequest payload, CancellationToken ct)
    {
        var info = await store.GetNameAndLogoAsync(clientId, ct);
        if (info is null) return NotFound(new { detail = "Client not found" });
        if ((payload.ConfirmClientName ?? "").Trim() != info.Value.Name.Trim())
            return BadRequest(new { detail = "El nombre de confirmacion no coincide con el del cliente." });

        var (deleted, logoBlobName) = await store.DeleteClientCascadeAsync(clientId, ct);

        if (!string.IsNullOrEmpty(logoBlobName))
        {
            try { await blobs.DeleteAsync(config.StorageContainerOutputs, logoBlobName!, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "No se pudo borrar logo del cliente eliminado {Id}", clientId); }
        }
        var total = deleted.Values.Sum();
        logger.LogWarning("Client deleted client_id={Id} by={By} total_rows={Total}", clientId, User.FindFirst("sub")?.Value, total);
        return Ok(new { message = "Cliente eliminado correctamente", client_id = clientId, client_name = info.Value.Name, deleted, total_rows = total });
    }

    // -------------------- PUT /clients/{id}/logo (admin/consultor) --------------------
    [HttpPut("{clientId:int}/logo")]
    [Authorize(Roles = Roles.Editors)]
    public async Task<IActionResult> UploadLogo(int clientId, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { detail = "El archivo de logo esta vacio." });
        if (file.Length > LogoMaxBytes)
            return BadRequest(new { detail = "El logo supera el limite de 2 MB." });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var content = ms.ToArray();
        if (content.Length == 0)
            return BadRequest(new { detail = "El archivo de logo esta vacio." });

        if (!TryDetectLogoType(content, out var contentType))
            return BadRequest(new { detail = "Formato no permitido. Use PNG, JPG o SVG." });

        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        if (!await store.ExistsAsync(clientId, ct))
            return NotFound(new { detail = "Client not found" });

        var blobName = LogoBlobName(clientId);
        await blobs.UploadAsync(config.StorageContainerOutputs, blobName, content, contentType, ct);
        await store.UpdateLogoMetaAsync(clientId, blobName, contentType, ct);
        return Ok(new { message = "Logo actualizado", content_type = contentType });
    }

    // -------------------- GET /clients/{id}/logo --------------------
    [HttpGet("{clientId:int}/logo")]
    public async Task<IActionResult> GetLogo(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var logo = await store.GetLogoAsync(clientId, ct);
        if (logo is null || string.IsNullOrEmpty(logo.Value.BlobName))
            return NotFound(new { detail = "El cliente no tiene logo" });

        byte[] data;
        try { data = await blobs.DownloadAsync(config.StorageContainerOutputs, logo.Value.BlobName!, ct); }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo leer el logo del cliente {Id}", clientId);
            return NotFound(new { detail = "El cliente no tiene logo" });
        }

        // Headers defensivos (paridad con FastAPI): el SVG no ejecuta script.
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Disposition"] = "inline";
        return File(data, logo.Value.ContentType ?? "application/octet-stream");
    }

    // -------------------- DELETE /clients/{id}/logo (admin/consultor) --------------------
    [HttpDelete("{clientId:int}/logo")]
    [Authorize(Roles = Roles.Editors)]
    public async Task<IActionResult> DeleteLogo(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var logo = await store.GetLogoAsync(clientId, ct);
        if (logo is null) return NotFound(new { detail = "Client not found" });
        if (!string.IsNullOrEmpty(logo.Value.BlobName))
            await blobs.DeleteAsync(config.StorageContainerOutputs, logo.Value.BlobName!, ct);
        await store.ClearLogoAsync(clientId, ct);
        return Ok(new { message = "Logo eliminado" });
    }

    // -------------------- helpers --------------------
    private static bool TryDetectLogoType(byte[] content, out string contentType)
    {
        if (content.Length >= 8 && content.AsSpan(0, 8).SequenceEqual(PngMagic)) { contentType = "image/png"; return true; }
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF) { contentType = "image/jpeg"; return true; }
        var head = System.Text.Encoding.ASCII.GetString(content, 0, Math.Min(content.Length, 2048)).TrimStart().ToLowerInvariant();
        if (head.StartsWith("<?xml") || head.Contains("<svg")) { contentType = "image/svg+xml"; return true; }
        contentType = "";
        return false;
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
