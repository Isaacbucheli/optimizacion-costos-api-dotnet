using System.Text.Json.Nodes;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Narrativa ejecutiva del informe mensual con Azure OpenAI. Port de app/reports/ai_narrative.py.
/// Reusa el IChatCompletionClient existente (NO construye un HttpClient nuevo para OpenAI) + AppConfig.
/// El prompt PROHIBE expresamente hablar de costos o montos: el informe de gestion no incluye cifras.
/// Si Azure OpenAI no esta configurado o el modelo no responde, devuelve null (el informe se genera
/// sin narrativa) — igual que el Optional[dict] del Python.
/// El "contexto" es el dict compacto que arma el builder (se serializa a JSON para el prompt).
/// </summary>
public interface IReportNarrativeService
{
    /// <summary>
    /// Genera la narrativa ejecutiva del informe detallado (generate_narrative).
    /// Devuelve null si Azure OpenAI no esta configurado o el modelo no respondio.
    /// </summary>
    Task<ReportNarrative?> GenerateNarrativeAsync(JsonNode context, CancellationToken ct = default);

    /// <summary>
    /// Genera titular, sintesis y lectura gerencial por dominio para el dashboard ejecutivo
    /// (generate_executive_narrative). Devuelve null si no esta configurado / no respondio.
    /// </summary>
    Task<ReportExecutiveNarrative?> GenerateExecutiveNarrativeAsync(JsonNode context, CancellationToken ct = default);
}
