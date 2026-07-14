using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Informe ejecutivo mensual por cliente. Port de app/routes/reports.py (prefix /reports).
/// La generación corre en background (cola + BackgroundService); el JSON vive en Blob y en SQL solo
/// queda el índice + el plan de acción editable. SIN COSTOS (el builder no incluye cifras).
/// </summary>
[ApiController]
[Authorize]
[Route("reports")]
[RequireModule(Modules.Report)]
public sealed class ReportsController(
    IReportStore store,
    IReportJobQueue queue,
    IBlobStorageService blobs,
    IAnalysisAccess access,
    IReportWordExporter wordExporter,
    IReportExecutive executive,
    IReportNarrativeService narrative,
    ISqlConnectionFactory factory,
    AppConfig config,
    ILogger<ReportsController> logger) : ControllerBase
{
    private const int MinYear = 2020;
    private static readonly string[] PlanPrioridades = ["Alta", "Media", "Baja"];
    private static readonly string[] PlanEstados = ["Pendiente", "En analisis", "En curso", "Resuelto", "Descartado"];
    private static readonly JsonSerializerOptions DumpOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ----------------------------------------------------------------- listado

    [HttpGet("clients/{clientId:int}")]
    public async Task<IActionResult> ListReports(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        if (!await store.ClientExistsAsync(clientId, ct)) return NotFound(new { detail = "Client not found" });

        var items = await store.ListAsync(clientId, ct);
        var reports = items.Select(it => new
        {
            report_id = it.ReportId,
            year = it.Year,
            month = it.Month,
            status = it.Status,
            is_partial = it.IsPartial,
            summary = ParseJson(it.SummaryJson),
            generated_by = it.GeneratedBy,
            generated_at = it.GeneratedAt,
        });
        return Ok(new { client_id = clientId, reports });
    }

    // ----------------------------------------------------------------- lectura del informe

    [HttpGet("clients/{clientId:int}/{year:int}/{month:int}")]
    public async Task<IActionResult> GetReport(int clientId, int year, int month, CancellationToken ct)
    {
        var load = await LoadCompletedReportAsync(clientId, year, month, ct);
        return load.Early ?? Ok(load.Report);
    }

    [HttpGet("clients/{clientId:int}/{year:int}/{month:int}/export/word")]
    public async Task<IActionResult> ExportWord(int clientId, int year, int month, CancellationToken ct)
    {
        var load = await LoadCompletedReportAsync(clientId, year, month, ct);
        if (load.Early is not null) return load.Early;

        var report = load.Report!;
        report["generated_by"] = load.GeneratedBy;
        try
        {
            var el = JsonSerializer.SerializeToElement(report, DumpOpts);
            var document = wordExporter.BuildWordReport(el);
            var filename = wordExporter.WordFilename(el);
            return File(document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", filename);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo generar el Word del informe client={Cid} {Y}-{M}", clientId, year, month);
            return StatusCode(500, new { detail = "No se pudo generar el documento Word" });
        }
    }

    [HttpGet("clients/{clientId:int}/{year:int}/{month:int}/executive")]
    public async Task<IActionResult> GetExecutive(int clientId, int year, int month, CancellationToken ct)
    {
        var load = await LoadCompletedReportAsync(clientId, year, month, ct);
        if (load.Early is not null) return load.Early;

        var el = JsonSerializer.SerializeToElement(load.Report, DumpOpts);
        return Ok(new
        {
            client = load.Report!["client"],
            period = load.Report["period"],
            semaforo = executive.ComputeSemaforo(el),
            narrative = load.Report["executive_narrative"],
        });
    }

    [HttpPost("clients/{clientId:int}/{year:int}/{month:int}/executive/narrative")]
    [RequireModule(Modules.Report, ModuleAccess.Edit)]
    public async Task<IActionResult> GenerateExecutiveNarrative(int clientId, int year, int month, CancellationToken ct)
    {
        var load = await LoadCompletedReportAsync(clientId, year, month, ct);
        if (load.Early is not null) return load.Early;

        var report = load.Report!;
        var el = JsonSerializer.SerializeToElement(report, DumpOpts);
        var semaforo = executive.ComputeSemaforo(el);
        var narr = await narrative.GenerateExecutiveNarrativeAsync(executive.BuildExecutiveContext(el, semaforo), ct);
        if (narr is null)
            return StatusCode(503, new
            {
                detail = "La narrativa IA no esta disponible en este momento. " +
                         "Verifica la configuracion de Azure OpenAI e intenta de nuevo.",
            });

        var narrNode = JsonSerializer.SerializeToNode(narr, DumpOpts)!.AsObject();
        narrNode["generated_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        narrNode["generated_by"] = User.FindFirst("sub")?.Value;
        report["executive_narrative"] = narrNode;
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(report.ToJsonString(DumpOpts));
            await blobs.UploadAsync(config.StorageContainerOutputs, ReportGenerationService.BlobName(clientId, year, month), bytes, "application/json", ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo persistir la narrativa gerencial client={Cid} {Y}-{M}", clientId, year, month);
            return StatusCode(500, new { detail = "No se pudo guardar la narrativa generada" });
        }
        return Ok(new { semaforo, narrative = narrNode });
    }

    // ----------------------------------------------------------------- plan de acción

    [HttpGet("clients/{clientId:int}/{year:int}/{month:int}/action-plan")]
    public async Task<IActionResult> GetActionPlan(int clientId, int year, int month, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var items = await store.ListActionPlanAsync(clientId, year, month, ct);
        var defaults = items.Count == 0 ? await DefaultPlanItemsAsync(clientId, year, month, ct) : [];
        var canEdit = User.IsInRole(Roles.Admin) || User.IsInRole(Roles.Consultor);
        return Ok(new
        {
            items = MapItems(items),
            defaults,
            can_edit = canEdit,
            estados = PlanEstados,
            prioridades = PlanPrioridades,
        });
    }

    [HttpPost("clients/{clientId:int}/{year:int}/{month:int}/action-plan/seed")]
    [RequireModule(Modules.Report, ModuleAccess.Edit)]
    public async Task<IActionResult> SeedActionPlan(int clientId, int year, int month, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var defaults = await DefaultPlanItemsAsync(clientId, year, month, ct);
        var existing = await store.ListActionPlanAsync(clientId, year, month, ct);
        if (existing.Count == 0 && defaults.Count > 0)
            await store.SeedActionPlanAsync(clientId, year, month, defaults.Select(ToWrite).ToList(), User.FindFirst("sub")?.Value, ct);

        var items = await store.ListActionPlanAsync(clientId, year, month, ct);
        return StatusCode(201, new { items = MapItems(items) });
    }

    [HttpPost("clients/{clientId:int}/{year:int}/{month:int}/action-plan")]
    [RequireModule(Modules.Report, ModuleAccess.Edit)]
    public async Task<IActionResult> CreateActionItem(int clientId, int year, int month, [FromBody] ActionPlanItemRequest payload, CancellationToken ct)
    {
        var error = Validate(payload);
        if (error is not null) return BadRequest(new { detail = error });
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        await store.CreateActionItemAsync(clientId, year, month, ToWrite(payload), User.FindFirst("sub")?.Value, ct);
        var items = await store.ListActionPlanAsync(clientId, year, month, ct);
        return StatusCode(201, new { items = MapItems(items) });
    }

    [HttpPut("clients/{clientId:int}/{year:int}/{month:int}/action-plan/{itemId:int}")]
    [RequireModule(Modules.Report, ModuleAccess.Edit)]
    public async Task<IActionResult> UpdateActionItem(int clientId, int year, int month, int itemId, [FromBody] ActionPlanItemRequest payload, CancellationToken ct)
    {
        var error = Validate(payload);
        if (error is not null) return BadRequest(new { detail = error });
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        var updated = await store.UpdateActionItemAsync(clientId, year, month, itemId, ToWrite(payload), User.FindFirst("sub")?.Value, ct);
        if (updated is null) return NotFound(new { detail = "Item no encontrado" });
        var items = await store.ListActionPlanAsync(clientId, year, month, ct);
        return Ok(new { items = MapItems(items) });
    }

    [HttpDelete("clients/{clientId:int}/{year:int}/{month:int}/action-plan/{itemId:int}")]
    [RequireModule(Modules.Report, ModuleAccess.Edit)]
    public async Task<IActionResult> DeleteActionItem(int clientId, int year, int month, int itemId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        if (!await store.DeleteActionItemAsync(clientId, year, month, itemId, ct))
            return NotFound(new { detail = "Item no encontrado" });
        var items = await store.ListActionPlanAsync(clientId, year, month, ct);
        return Ok(new { items = MapItems(items) });
    }

    // ----------------------------------------------------------------- generación

    [HttpPost("clients/{clientId:int}/generate")]
    [RequireModule(Modules.Report, ModuleAccess.Edit)]
    public async Task<IActionResult> GenerateReport(int clientId, [FromBody] GenerateReportRequest payload, CancellationToken ct)
    {
        if (payload.Year is < MinYear or > 2100) return BadRequest(new { detail = "year fuera de rango" });
        if (payload.Month is < 1 or > 12) return BadRequest(new { detail = "month fuera de rango" });
        var now = DateTime.UtcNow;
        if ((payload.Year, payload.Month).CompareTo((now.Year, now.Month)) > 0)
            return BadRequest(new { detail = "El periodo no puede ser un mes futuro" });

        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);
        if (!await store.ClientExistsAsync(clientId, ct)) return NotFound(new { detail = "Client not found" });

        var (credentialId, credError) = await ResolveCredentialIdAsync(clientId, payload.CredentialId, ct);
        if (credError is not null) return credError;

        var generatedBy = User.FindFirst("sub")?.Value;
        var blobName = ReportGenerationService.BlobName(clientId, payload.Year, payload.Month);
        await store.UpsertPlaceholderAsync(clientId, payload.Year, payload.Month, config.StorageContainerOutputs, blobName, generatedBy, ct);

        queue.Enqueue(new ReportJob(clientId, payload.Year, payload.Month, credentialId!.Value, payload.IncludeNarrative, generatedBy));

        return StatusCode(202, new
        {
            client_id = clientId,
            year = payload.Year,
            month = payload.Month,
            status = "generating",
            message = $"La generacion del informe ha iniciado. Consulta GET /reports/clients/{clientId}/{payload.Year}/{payload.Month:D2} para obtener el resultado.",
        });
    }

    // ----------------------------------------------------------------- helpers

    private sealed record LoadResult(IActionResult? Early, JsonObject? Report, string? GeneratedBy);

    private async Task<LoadResult> LoadCompletedReportAsync(int clientId, int year, int month, CancellationToken ct)
    {
        if (month is < 1 or > 12 || year < MinYear)
            return new LoadResult(BadRequest(new { detail = "Periodo invalido" }), null, null);

        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return new LoadResult(Translate(chk), null, null);

        var reference = await store.GetBlobRefAsync(clientId, year, month, ct);
        if (reference is null)
            return new LoadResult(NotFound(new { detail = "No hay informe para ese periodo" }), null, null);
        if (reference.Status == "generating")
            return new LoadResult(StatusCode(202, new { status = "generating", message = "El informe se esta generando, intenta en unos minutos" }), null, null);
        if (reference.Status == "failed")
        {
            var error = "La generacion del informe fallo";
            if (!string.IsNullOrEmpty(reference.SummaryJson))
                try { error = JsonNode.Parse(reference.SummaryJson)?["error"]?.GetValue<string>() ?? error; } catch { /* summary corrupto */ }
            return new LoadResult(StatusCode(500, new { detail = error }), null, null);
        }
        if (reference.Status != "completed")
            return new LoadResult(NotFound(new { detail = "No hay informe generado para ese periodo" }), null, null);

        try
        {
            var payload = await blobs.DownloadAsync(reference.Container, reference.BlobName, ct);
            var report = JsonNode.Parse(payload)?.AsObject() ?? new JsonObject();
            return new LoadResult(null, report, reference.GeneratedBy);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo leer el blob del informe client={Cid} {Y}-{M}", clientId, year, month);
            return new LoadResult(StatusCode(500, new { detail = "No se pudo leer el informe almacenado" }), null, null);
        }
    }

    private async Task<IReadOnlyList<ActionPlanSeed>> DefaultPlanItemsAsync(int clientId, int year, int month, CancellationToken ct)
    {
        var load = await LoadCompletedReportAsync(clientId, year, month, ct);
        if (load.Early is not null || load.Report is null) return [];
        var el = JsonSerializer.SerializeToElement(load.Report, DumpOpts);
        return executive.BuildDefaultActionPlan(el, executive.ComputeSemaforo(el));
    }

    private async Task<(int? Id, IActionResult? Error)> ResolveCredentialIdAsync(int clientId, int? credentialId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (credentialId is not null)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM dbo.client_azure_credentials WHERE credential_id = @c AND client_id = @cid AND is_active = 1";
            cmd.Parameters.Add(new SqlParameter("@c", credentialId.Value));
            cmd.Parameters.Add(new SqlParameter("@cid", clientId));
            if (await cmd.ExecuteScalarAsync(ct) is null)
                return (null, BadRequest(new { detail = "La credencial indicada no pertenece al cliente o esta inactiva" }));
            return (credentialId, null);
        }

        await using var list = conn.CreateCommand();
        list.CommandText = """
            SELECT credential_id FROM dbo.client_azure_credentials
            WHERE client_id = @cid AND is_active = 1 ORDER BY created_at DESC
            """;
        list.Parameters.Add(new SqlParameter("@cid", clientId));
        var ids = new List<int>();
        await using (var r = await list.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) ids.Add(r.GetInt32(0));

        if (ids.Count == 0) return (null, BadRequest(new { detail = "El cliente no tiene credenciales Azure activas" }));
        if (ids.Count > 1) return (null, BadRequest(new { detail = "El cliente tiene varias credenciales activas; indica credential_id" }));
        return (ids[0], null);
    }

    private static IEnumerable<object> MapItems(IReadOnlyList<ActionPlanItem> items) => items.Select(it => new
    {
        item_id = it.ItemId,
        orden = it.Orden,
        prioridad = it.Prioridad,
        hallazgo = it.Hallazgo,
        accion = it.Accion,
        responsable = it.Responsable,
        estado = it.Estado,
        updated_by = it.UpdatedBy,
        updated_at = it.UpdatedAt,
    });

    private static ActionPlanWrite ToWrite(ActionPlanItemRequest p) =>
        new(p.Orden ?? 0, p.Prioridad, p.Hallazgo, p.Accion, p.Responsable, string.IsNullOrEmpty(p.Estado) ? "Pendiente" : p.Estado);

    private static ActionPlanWrite ToWrite(ActionPlanSeed s) =>
        new(s.Orden, s.Prioridad, s.Hallazgo, s.Accion, s.Responsable, string.IsNullOrEmpty(s.Estado) ? "Pendiente" : s.Estado);

    // Validación equivalente a los Field(...) de Pydantic en ActionPlanItemRequest.
    private static string? Validate(ActionPlanItemRequest p)
    {
        if (!PlanPrioridades.Contains(p.Prioridad)) return "prioridad invalida (Alta, Media, Baja)";
        var hallazgo = p.Hallazgo ?? "";
        if (hallazgo.Length is < 3 or > 500) return "hallazgo debe tener entre 3 y 500 caracteres";
        if (p.Accion is { Length: > 500 }) return "accion no puede superar 500 caracteres";
        if (p.Responsable is { Length: > 120 }) return "responsable no puede superar 120 caracteres";
        var estado = string.IsNullOrEmpty(p.Estado) ? "Pendiente" : p.Estado;
        if (!PlanEstados.Contains(estado)) return "estado invalido";
        if (p.Orden is < 0 or > 999) return "orden fuera de rango (0-999)";
        return null;
    }

    private static JsonNode? ParseJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonNode.Parse(json); } catch { return null; }
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}

/// <summary>Body de POST /reports/clients/{id}/generate (port de GenerateReportRequest).</summary>
public sealed record GenerateReportRequest(int Year, int Month, int? CredentialId = null, bool IncludeNarrative = true);

/// <summary>Body del plan de acción (port de ActionPlanItemRequest). orden/estado opcionales.</summary>
public sealed record ActionPlanItemRequest(string Prioridad, string Hallazgo, string? Accion, string? Responsable, string? Estado, int? Orden);
