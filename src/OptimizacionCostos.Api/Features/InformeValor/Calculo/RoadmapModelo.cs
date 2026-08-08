using System.Text.Json.Serialization;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Bloque de roadmap: matriz WAF. Nombres sacados de <c>calcMatriz</c> en
/// <c>docs/Plantilla-Dashboard-BIT.html</c>; <c>D.matriz</c> en el modelo embebido. Contrato de la
/// Tarea 2 del plan de la entrega 2b: la Tarea 7 (sin decisión propia de la Parte 1; usa los
/// helpers comunes: <see cref="Redondeo"/>, <see cref="Fechas"/>) implementa el cálculo, a partir
/// de <see cref="MatrizFila"/>.
///
/// <para><b>El ámbito ya no es texto libre.</b> La plantilla agrupa por una columna "Ámbito" de
/// Excel sin controlar. <see cref="MatrizFila.Ambito"/> (Recolector, entrega 2a) es en realidad la
/// etiqueta de PILAR (misma tabla que <see cref="AdvisorRecolector.EtiquetaPilar"/>),
/// no la columna cruda: <see cref="Ambitos"/> agrupa por esa etiqueta, así que "ámbito" en este
/// contrato significa pilar Well-Architected, con vocabulario controlado por construcción, no una
/// celda de Excel que el consultor podría escribir de cinco formas distintas para el mismo pilar.
/// </para>
///
/// <para><see cref="MatrizFila.Prioridad"/> llega cruda ("1"/"2"/"3", sin la etiqueta
/// "1 - ALTA" que arma el exportador de Excel): si <see cref="RoadmapItem.Prioridad"/> necesita la
/// etiqueta traducida es una decisión de presentación de la Tarea 7, no de este contrato.</para>
///
/// <para><b>Filas.</b> <see cref="Items"/> es una lista de objetos con nombre
/// (<see cref="RoadmapItem"/>): la plantilla ya lee sus campos por nombre en la tabla priorizada
/// (<c>x.a</c>, <c>x.t</c>, <c>x.v</c>...), no por posición, así que no hay ganancia en aplanarlos
/// a arreglo y sí se pierde legibilidad. <see cref="Ambitos"/> es igual, objetos con nombre.</para>
/// </summary>
public sealed record RoadmapModelo(
    [property: JsonPropertyName("n")] int Total,
    [property: JsonPropertyName("items")] IReadOnlyList<RoadmapItem> Items,
    [property: JsonPropertyName("amb")] IReadOnlyList<RoadmapAmbito> Ambitos,
    [property: JsonPropertyName("cerrados")] int Cerrados,
    [property: JsonPropertyName("curso")] int EnCurso,
    [property: JsonPropertyName("sinIniciar")] int SinIniciar,
    [property: JsonPropertyName("avance")] double AvancePromedio,
    [property: JsonPropertyName("horas")] decimal HorasPendientes);

/// <summary>Un hallazgo de la matriz (<c>items</c> de <c>calcMatriz</c>).</summary>
public sealed record RoadmapItem(
    [property: JsonPropertyName("a")] string Ambito,
    [property: JsonPropertyName("t")] string Hallazgo,
    [property: JsonPropertyName("f")] string? Fecha,
    [property: JsonPropertyName("i")] int Impacto,
    [property: JsonPropertyName("p")] string? Prioridad,
    [property: JsonPropertyName("e")] decimal Esfuerzo,
    [property: JsonPropertyName("v")] int AvancePct,
    [property: JsonPropertyName("n")] int RecomendacionesAsociadas,
    [property: JsonPropertyName("g")] string? Registro);

/// <summary>Avance agregado por ámbito (<c>amb</c> de <c>calcMatriz</c>).</summary>
public sealed record RoadmapAmbito(
    [property: JsonPropertyName("n")] string Nombre,
    [property: JsonPropertyName("c")] int Cantidad,
    [property: JsonPropertyName("rec")] int Recomendaciones,
    [property: JsonPropertyName("av")] int AvancePromedio);
