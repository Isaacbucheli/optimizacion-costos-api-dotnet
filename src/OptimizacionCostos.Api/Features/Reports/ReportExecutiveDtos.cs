using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.Reports;

/// <summary>
/// Dominio del semaforo (port de executive.py::_domain). Claves JSON EXACTAS del Python.
/// 'detalle' es un objeto cuyas claves varian por dominio (port directo del dict del Python),
/// por eso se modela como JsonObject.
/// </summary>
public sealed record SemaforoDomain(
    [property: JsonPropertyName("dominio")] string Dominio,
    [property: JsonPropertyName("etiqueta")] string Etiqueta,
    [property: JsonPropertyName("estado")] string Estado,
    [property: JsonPropertyName("lectura")] string Lectura,
    [property: JsonPropertyName("criterio")] string Criterio,
    [property: JsonPropertyName("detalle")] JsonObject Detalle);

/// <summary>
/// Resultado de compute_semaforo: umbral_presion + estado_general (el peor de los dominios) +
/// la lista de dominios presentes.
/// </summary>
public sealed record Semaforo(
    [property: JsonPropertyName("umbral_presion")] double UmbralPresion,
    [property: JsonPropertyName("estado_general")] string EstadoGeneral,
    [property: JsonPropertyName("dominios")] IReadOnlyList<SemaforoDomain> Dominios);

/// <summary>
/// Item sembrado del plan de accion (port de build_default_action_plan). Claves EXACTAS: prioridad,
/// hallazgo, accion, responsable, orden, estado. Mapea 1:1 a ActionPlanWrite del store
/// (Orden/Prioridad/Hallazgo/Accion/Responsable/Estado).
/// </summary>
public sealed record ActionPlanSeed(
    [property: JsonPropertyName("prioridad")] string Prioridad,
    [property: JsonPropertyName("hallazgo")] string Hallazgo,
    [property: JsonPropertyName("accion")] string Accion,
    [property: JsonPropertyName("responsable")] string Responsable,
    [property: JsonPropertyName("orden")] int Orden,
    [property: JsonPropertyName("estado")] string Estado);
