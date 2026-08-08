using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// El modelo completo del informe: <see cref="Meta"/> más los cinco bloques —consumo, operación,
/// seguridad, postura y roadmap— y <see cref="CatSerie"/>. Forma exacta del objeto <c>D</c> que
/// arma <c>recalcula()</c> en <c>docs/Plantilla-Dashboard-BIT.html</c>: <c>D.meta</c>,
/// <c>D.tickets</c>, <c>D.fact</c>, <c>D.rbac</c>, <c>D.advisor</c>, <c>D.matriz</c>,
/// <c>D.catSerie</c>. Todo bloque ausente en la plantilla es <c>null</c> (insumo no cargado):
/// <c>render()</c> ya maneja ese caso con <c>if(!t){ ...pendiente... }</c> por sección, así que
/// cada propiedad de bloque acá es nullable, nunca un objeto "vacío" que simule ausencia.
///
/// <para>Lo ensambla la Tarea 8 (fuera de esta entrega 2b, parte 1 y 2), que también resuelve D12
/// (las tres cifras de suscripciones se concilian) sobre este objeto: cruza las suscripciones que
/// ve cada bloque y publica el conjunto unión en la sección de cobertura del informe. Esa sección
/// de cobertura, el resumen ejecutivo, "próximos pasos" y "trazabilidad de las cifras" que arma
/// <c>render()</c> hoy inline (mezclando los cinco bloques) tampoco son parte de este contrato:
/// son síntesis que produce el ensamblador a partir de los cinco bloques ya calculados, no un
/// campo de ningún bloque individual.</para>
///
/// <para><b>Serializar con <see cref="InformeValorJsonOptions.Instance"/>, nunca con las opciones
/// globales de la API</b> (D13: la política global transforma a snake_case tanto nombres de
/// propiedad como claves de diccionario, y <c>render()</c> espera los nombres tal cual salen del
/// JavaScript original).</para>
/// </summary>
public sealed record ModeloInformeValor(
    [property: JsonPropertyName("meta")] InformeValorMeta Meta,
    [property: JsonPropertyName("tickets")] OperacionModelo? Operacion,
    [property: JsonPropertyName("fact")] ConsumoModelo? Consumo,
    [property: JsonPropertyName("rbac")] SeguridadModelo? Seguridad,
    [property: JsonPropertyName("advisor")] PosturaModelo? Postura,
    [property: JsonPropertyName("matriz")] RoadmapModelo? Roadmap,
    [property: JsonPropertyName("catSerie")] IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>>? CatSerie);

/// <summary>
/// Encabezado del informe (<c>D.meta</c>). <see cref="Corte"/> es la fecha de corte tal como
/// llegó resuelta en <see cref="ContextoInformeValor.Corte"/> (<c>"aaaa-MM-dd"</c>), NO se
/// recalcula acá: es la misma restricción de las Global Constraints, un valor congelado en el
/// momento de generar, no un reloj. <see cref="Periodo"/> es texto libre en la plantilla (el
/// consultor lo escribe a mano y no se contrasta contra nada); en este módulo puede seguir siendo
/// una etiqueta descriptiva del rango, porque el rango real que SÍ filtra los datos
/// (<see cref="ContextoInformeValor.PeriodStart"/>/<see cref="ContextoInformeValor.PeriodEnd"/>,
/// D0) vive en la entrega de la base de datos, no en este texto.
/// </summary>
public sealed record InformeValorMeta(
    [property: JsonPropertyName("cliente")] string Cliente,
    [property: JsonPropertyName("periodo")] string Periodo,
    [property: JsonPropertyName("corte")] string Corte);
