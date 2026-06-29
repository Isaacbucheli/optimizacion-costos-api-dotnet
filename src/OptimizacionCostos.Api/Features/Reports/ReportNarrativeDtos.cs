using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Narrativa ejecutiva del informe detallado (port de app/reports/ai_narrative.py::_normalize).
/// Las claves JSON replican EXACTAMENTE las del Python (resumen_ejecutivo, hallazgos,
/// performance_comentario, backups_comentario, disponibilidad_comentario, conclusiones,
/// recomendaciones) para que el front existente y el JSON del informe no cambien.
/// Listas nunca null (el _normalize devuelve [] cuando no hay datos).
/// </summary>
public sealed record ReportNarrative(
    [property: JsonPropertyName("resumen_ejecutivo")] string ResumenEjecutivo,
    [property: JsonPropertyName("hallazgos")] IReadOnlyList<string> Hallazgos,
    [property: JsonPropertyName("performance_comentario")] string PerformanceComentario,
    [property: JsonPropertyName("backups_comentario")] string BackupsComentario,
    [property: JsonPropertyName("disponibilidad_comentario")] string DisponibilidadComentario,
    [property: JsonPropertyName("conclusiones")] IReadOnlyList<string> Conclusiones,
    [property: JsonPropertyName("recomendaciones")] IReadOnlyList<string> Recomendaciones);

/// <summary>
/// Narrativa gerencial del dashboard ejecutivo (port de
/// app/reports/ai_narrative.py::_exec_normalize). Claves: titular, sintesis, lectura_dominios.
/// lectura_dominios es un dict {dominio -> texto} solo con los dominios presentes en EXEC_DOMAINS
/// que tengan texto (igual que el Python).
/// </summary>
public sealed record ReportExecutiveNarrative(
    [property: JsonPropertyName("titular")] string Titular,
    [property: JsonPropertyName("sintesis")] string Sintesis,
    [property: JsonPropertyName("lectura_dominios")] IReadOnlyDictionary<string, string> LecturaDominios);
