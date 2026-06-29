using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Vista gerencial del informe mensual: semaforo por dominio. Port de app/reports/executive.py.
/// NO consulta Azure ni SQL ni recalcula metricas: opera solo sobre el JSON del informe ya generado
/// (una sola fuente de datos para evitar inconsistencias con el informe detallado). El "report" es
/// el objeto JSON del informe (claves del builder.py); se recibe como JsonElement.
/// </summary>
public interface IReportExecutive
{
    /// <summary>compute_semaforo: semaforo por dominio + estado general (el peor de los dominios).</summary>
    Semaforo ComputeSemaforo(JsonElement report);

    /// <summary>
    /// build_default_action_plan: hallazgos automaticos para sembrar el plan de accion editable
    /// (misma logica que las 'Top prioridades' del dashboard).
    /// </summary>
    IReadOnlyList<ActionPlanSeed> BuildDefaultActionPlan(JsonElement report, Semaforo semaforo);

    /// <summary>
    /// build_executive_context: contexto compacto para la narrativa gerencial IA (solo numeros que ya
    /// estan en el informe). Devuelve un JsonNode para alimentar GenerateExecutiveNarrativeAsync.
    /// </summary>
    JsonObject BuildExecutiveContext(JsonElement report, Semaforo semaforo);
}
