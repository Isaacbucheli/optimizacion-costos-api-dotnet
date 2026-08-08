using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Bloque de operación: mesa de servicio (insumo "casos"). Nombres de campo sacados de
/// <c>calcTickets</c> en <c>docs/Plantilla-Dashboard-BIT.html</c> (fuente de verdad de qué
/// consume <c>render()</c>); <c>D.tickets</c> en el modelo embebido. Contrato de la Tarea 2 del
/// plan de la entrega 2b: la Tarea 4 (D0, D1, D2, D10) implementa el cálculo contra esta forma.
///
/// <para><b>D2 cambia la forma, a propósito.</b> La plantilla mide "dentro de SLA" de tres formas
/// distintas en la misma página (el KPI exige <c>SI</c>/<c>SÍ</c>/<c>YES</c> exacto; el promedio
/// "dentro de SLA" incluye a los que tienen la celda vacía; la tabla de detalle pinta "Sí" todo lo
/// que no sea "NO"). La regla nueva es un tercer estado explícito: <see cref="Cumple"/> +
/// <see cref="NoCumple"/> + <see cref="SinEvaluar"/> = <see cref="Total"/>, y
/// <see cref="PctCumplimiento"/> se calcula sobre <see cref="Cumple"/> + <see cref="NoCumple"/>
/// —ese denominador declarado explícitamente en <see cref="DenominadorPctCumplimiento"/>—, nunca
/// sobre <see cref="Total"/>. La plantilla no tiene <c>sinEvaluar</c>/<c>cumple</c>/<c>noCumple</c>
/// como campos propios (los deriva de <c>si</c>/<c>no</c> con las tres reglas contradictorias de
/// arriba): quien porte <c>render()</c> en la entrega 3 tiene que leer de estos tres campos, no de
/// <c>si</c>/<c>no</c> binarios.</para>
///
/// <para><b>D1 (denominador por sección) no cambia la forma, cambia la obligación de quien
/// calcula.</b> La decisión nombra explícitamente <c>cats</c>, <c>frentes</c> y <c>hor</c> —
/// <see cref="Categorias"/>, <see cref="Frentes"/> y <see cref="PorHorario"/> acá—: cada una tiene
/// que sumar exactamente su propio denominador entre sus elementos (<see cref="Total"/> para las
/// dos primeras; el total de casos con horario para la tercera), incluyendo una categoría/frente/
/// horario residual explícito ("sin categoría", "sin horario") en vez de filtrar los vacíos, y
/// ningún porcentaje de este bloque se calcula con el denominador de otra sección. No hay un campo
/// nuevo que lo fuerce: es una invariante a probar con un test dedicado en la Tarea 4, no algo que
/// el compilador pueda verificar por la forma del contrato.</para>
///
/// <para><b>D10 agrega <see cref="CasosSinSubcategoria"/>.</b> Hoy un caso sin subcategoría se
/// cuenta como proactivo por omisión (no matchea la expresión regular de "reactivo", así que cae
/// del lado proactivo sin que nadie lo haya decidido). La regla nueva los saca del numerador
/// proactivo; este campo es la cuenta que hace esa exclusión auditable en vez de silenciosa.</para>
///
/// <para><b>Las listas de filas posicionales</b> (<see cref="SerieMensual"/>, <see cref="PorHorario"/>,
/// <see cref="FueraDeSla"/>, <see cref="Detalle"/>) mantienen la forma de arreglo de la plantilla
/// en vez de convertirse a objetos con nombre: son arreglos JSON, así que ninguna política de
/// nombres de propiedad ni de claves de diccionario los puede tocar, y <c>render()</c> ya las lee
/// por posición (<c>r[0]</c>, <c>r[1]</c>...). Posiciones:
/// <see cref="SerieMensual"/> = [mes "aaaa-MM", total del mes, casos fuera de SLA del mes];
/// <see cref="PorHorario"/> = [nombre del horario, cantidad de casos];
/// <see cref="FueraDeSla"/> = [caso, fecha ISO o "", categoría, subcategoría, SLA en horas, duración en horas];
/// <see cref="Detalle"/> = [caso, fecha ISO o "", categoría, subcategoría, SLA en horas, duración en horas,
/// "SI"/"NO"/"SIN EVALUAR", horario]. La séptima posición son los mismos tres estados de D2
/// (<see cref="Cumple"/>/<see cref="NoCumple"/>/<see cref="SinEvaluar"/>), nunca el binario "SI"/"NO"
/// que forzaba a los casos sin evaluar a mostrarse como cumplidos: es la posición exacta de la que
/// habla el segundo defecto de D2 ("la tabla de detalle pinta 'Sí' todo lo que no sea 'NO'"), así
/// que dejarla en dos valores reintroduciría ese defecto en el propio contrato.
/// </para>
/// </summary>
public sealed record OperacionModelo(
    [property: JsonPropertyName("n")] int Total,
    [property: JsonPropertyName("cumple")] int Cumple,
    [property: JsonPropertyName("noCumple")] int NoCumple,
    [property: JsonPropertyName("sinEvaluar")] int SinEvaluar,
    [property: JsonPropertyName("pct")] double PctCumplimiento,
    [property: JsonPropertyName("denominadorPct")] int DenominadorPctCumplimiento,
    [property: JsonPropertyName("cerrados")] int Cerrados,
    [property: JsonPropertyName("media")] double MediaHoras,
    [property: JsonPropertyName("mediana")] double MedianaHoras,
    [property: JsonPropertyName("p90")] double P90Horas,
    [property: JsonPropertyName("mediaOk")] double MediaHorasDentroSla,
    [property: JsonPropertyName("enDias")] bool DuracionOriginalEnDias,
    [property: JsonPropertyName("cats")] IReadOnlyList<OperacionCategoria> Categorias,
    [property: JsonPropertyName("meses")] IReadOnlyList<IReadOnlyList<object?>> SerieMensual,
    [property: JsonPropertyName("racha")] int RachaMesesSinIncumplir,
    [property: JsonPropertyName("rachaCasos")] int RachaCasos,
    [property: JsonPropertyName("frentes")] IReadOnlyList<OperacionFrente> Frentes,
    [property: JsonPropertyName("nFrentes")] int TotalFrentes,
    [property: JsonPropertyName("nFrentesR")] int FrentesReactivos,
    [property: JsonPropertyName("casosR")] int CasosReactivos,
    [property: JsonPropertyName("casosSinSubcategoria")] int CasosSinSubcategoria,
    [property: JsonPropertyName("hor")] IReadOnlyList<IReadOnlyList<object?>> PorHorario,
    [property: JsonPropertyName("desde")] string? Desde,
    [property: JsonPropertyName("hasta")] string? Hasta,
    [property: JsonPropertyName("fuera")] IReadOnlyList<IReadOnlyList<object?>> FueraDeSla,
    [property: JsonPropertyName("lista")] IReadOnlyList<IReadOnlyList<object?>> Detalle);

/// <summary>Una categoría de caso (<c>cats</c> de <c>calcTickets</c>).</summary>
public sealed record OperacionCategoria(
    [property: JsonPropertyName("n")] string Nombre,
    [property: JsonPropertyName("c")] int Cantidad,
    [property: JsonPropertyName("f")] int FueraDeSla,
    [property: JsonPropertyName("med")] double MedianaHoras);

/// <summary>Un frente de trabajo, por subcategoría (<c>frentes</c> de <c>calcTickets</c>).</summary>
public sealed record OperacionFrente(
    [property: JsonPropertyName("n")] string Nombre,
    [property: JsonPropertyName("c")] int Cantidad,
    [property: JsonPropertyName("r")] bool EsReactivo);
