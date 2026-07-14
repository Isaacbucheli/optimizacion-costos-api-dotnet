using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.CostEngine.Api;

/// <summary>
/// CRUD de evaluaciones (cost_analysis) por cliente. Port de app/routes/analysis.py (prefix /analysis).
/// Antes seguía en FastAPI (estrangulador); se migra para que el stack nuevo sea todo-.NET.
/// El listado se filtra por los clientes accesibles del usuario; crear/asegurar exige acceso al cliente.
/// </summary>
[ApiController]
[Authorize]
[Route("analysis")]
[RequireModule(Modules.Costos)]
public sealed class AnalysisController(
    ISqlConnectionFactory factory,
    IAnalysisAccess access,
    ILogger<AnalysisController> logger) : ControllerBase
{
    public sealed record AnalysisCreateRequest(int ClientId, string? AnalysisName, string? Notes);

    private const string SelectColumns = """
        SELECT ca.analysis_id, ca.client_id, c.client_name, ca.analysis_name,
               ca.status, ca.currency_code, ca.pricing_source, ca.created_at
        FROM dbo.cost_analysis ca
        INNER JOIN dbo.clients c ON ca.client_id = c.client_id
        """;

    // -------------------- GET /analysis --------------------
    [HttpGet("")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var allowed = await access.AccessibleClientIdsAsync(User, ct); // null = admin (todos)

        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " ORDER BY ca.created_at DESC";

        var list = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var clientId = r.GetInt32(1);
            if (allowed is not null && !allowed.Contains(clientId)) continue;
            list.Add(MapRow(r));
        }
        return Ok(list);
    }

    // -------------------- POST /analysis/client/{clientId}/current --------------------
    [HttpPost("client/{clientId:int}/current")]
    [RequireModule(Modules.Costos, ModuleAccess.Edit)]
    public async Task<IActionResult> EnsureCurrent(int clientId, CancellationToken ct)
    {
        var chk = await access.AssertClientAccessAsync(User, clientId, ct);
        if (!chk.Ok) return Translate(chk);

        try
        {
            await using var conn = await factory.OpenAsync(ct);

            // La evaluación más reciente del cliente, si existe.
            await using (var sel = conn.CreateCommand())
            {
                sel.CommandText = SelectColumns + " WHERE ca.client_id = @cid ORDER BY ca.created_at DESC";
                sel.Parameters.Add(new SqlParameter("@cid", clientId));
                await using var r = await sel.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) return Ok(MapRow(r));
            }

            // No hay: validar que el cliente exista y crear una "Evaluacion actual".
            await using (var exists = conn.CreateCommand())
            {
                exists.CommandText = "SELECT 1 FROM dbo.clients WHERE client_id = @cid";
                exists.Parameters.Add(new SqlParameter("@cid", clientId));
                if (await exists.ExecuteScalarAsync(ct) is null)
                    return NotFound(new { detail = "Client not found" });
            }

            int newId;
            await using (var ins = conn.CreateCommand())
            {
                ins.CommandText = """
                    INSERT INTO dbo.cost_analysis (client_id, analysis_name, notes)
                    OUTPUT INSERTED.analysis_id
                    VALUES (@cid, @name, @notes)
                    """;
                ins.Parameters.Add(new SqlParameter("@cid", clientId));
                ins.Parameters.Add(new SqlParameter("@name", "Evaluacion actual"));
                ins.Parameters.Add(new SqlParameter("@notes", "Analisis operativo creado automaticamente para el tablero por cliente."));
                newId = Convert.ToInt32(await ins.ExecuteScalarAsync(ct));
            }

            await using (var get = conn.CreateCommand())
            {
                get.CommandText = SelectColumns + " WHERE ca.analysis_id = @id";
                get.Parameters.Add(new SqlParameter("@id", newId));
                await using var r = await get.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                return Ok(MapRow(r));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error asegurando evaluación actual client={Cid}", clientId);
            return StatusCode(500, new { detail = "Error interno del servidor" });
        }
    }

    // -------------------- POST /analysis --------------------
    [HttpPost("")]
    [RequireModule(Modules.Costos, ModuleAccess.Edit)]
    public async Task<IActionResult> Create([FromBody] AnalysisCreateRequest payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload.AnalysisName))
            return BadRequest(new { detail = "analysis_name es obligatorio" });
        var chk = await access.AssertClientAccessAsync(User, payload.ClientId, ct);
        if (!chk.Ok) return Translate(chk);

        try
        {
            await using var conn = await factory.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dbo.cost_analysis (client_id, analysis_name, notes)
                OUTPUT INSERTED.analysis_id
                VALUES (@cid, @name, @notes)
                """;
            cmd.Parameters.Add(new SqlParameter("@cid", payload.ClientId));
            cmd.Parameters.Add(new SqlParameter("@name", payload.AnalysisName));
            cmd.Parameters.Add(new SqlParameter("@notes", (object?)payload.Notes ?? DBNull.Value));
            var newId = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            return Ok(new { message = "Análisis creado correctamente", analysis_id = newId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creando análisis client={Cid}", payload.ClientId);
            return StatusCode(500, new { detail = "Error interno del servidor" });
        }
    }

    // -------------------- helpers --------------------
    private static object MapRow(SqlDataReader r) => new
    {
        analysis_id = r.GetInt32(0),
        client_id = r.GetInt32(1),
        client_name = r.IsDBNull(2) ? null : r.GetString(2),
        analysis_name = r.IsDBNull(3) ? null : r.GetString(3),
        status = r.IsDBNull(4) ? null : r.GetString(4),
        currency_code = r.IsDBNull(5) ? null : r.GetString(5),
        pricing_source = r.IsDBNull(6) ? null : r.GetString(6),
        created_at = FmtDate(r, 7),
    };

    // str(datetime) de Python: omite la fracción cuando es 0; si no, 6 dígitos.
    private static string FmtDate(SqlDataReader r, int i)
    {
        if (r.IsDBNull(i)) return "";
        if (r.GetValue(i) is DateTime dt)
        {
            var micro = (dt.Ticks % TimeSpan.TicksPerSecond) / 10;
            return micro == 0
                ? dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        }
        return r.GetValue(i).ToString() ?? "";
    }

    private IActionResult Translate(AccessCheck check) => check.Result switch
    {
        AccessResult.NotFound => NotFound(new { detail = check.Detail ?? "Not found" }),
        AccessResult.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { detail = check.Detail ?? "No tiene acceso a este cliente" }),
        _ => Ok(),
    };
}
