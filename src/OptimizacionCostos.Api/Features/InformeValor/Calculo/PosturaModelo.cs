using System.Text.Json.Serialization;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Bloque de postura: Azure Advisor + retiros de Azure. Nombres sacados de <c>calcAdvisor</c> en
/// <c>docs/Plantilla-Dashboard-BIT.html</c>; <c>D.advisor</c> en el modelo embebido (los retiros
/// viven adentro, en <see cref="Retiros"/>, igual que hoy). Contrato de la Tarea 2 del plan de la
/// entrega 2b: la Tarea 6 (D7, D8, D11, D13) implementa el cálculo, a partir de
/// <see cref="AdvisorFila"/> y <see cref="RetiroFila"/>.
///
/// <para><b>D8: pilar e impacto salen de los campos numéricos, nunca de texto.</b> Ya resuelto por
/// el recolector: <see cref="AdvisorFila.PillarNumber"/>/<see cref="AdvisorFila.Pilar"/> y
/// <see cref="AdvisorFila.ImpactNumber"/>/<see cref="AdvisorFila.Impacto"/> reemplazan a
/// <c>advisor_category</c>/<c>business impact</c> crudos. <see cref="Pilares"/> agrupa por
/// <see cref="AdvisorFila.Pilar"/>, no por la categoría cruda de Azure.</para>
///
/// <para><b>D7: la tabla de criterio técnico se autoexplica.</b> Hoy <c>savLineas</c> lista las
/// líneas ya deduplicadas y el pie de tabla imprime <c>bruto</c> sin deduplicar (el hallazgo más
/// fácil de encontrar con Excel: las filas visibles no suman el total impreso). Y el veredicto por
/// fila compara el monto de UNA línea contra la SUMA de las líneas de reserva de la suscripción,
/// así que con dos o más recomendaciones ninguna iguala la suma y las tres salen "descartadas".
/// La regla nueva: <see cref="AhorroBruto"/> se explica aparte (ya no es el pie de
/// <see cref="LineasAhorro"/>: las filas visibles suman <see cref="AhorroRealizable"/>, con
/// <see cref="AhorroDescartado"/> como la diferencia nombrada), y cada
/// <see cref="PosturaLineaAhorro.Contada"/> se deriva del MISMO cálculo por suscripción que
/// produce <see cref="AhorroRealizable"/> (el máximo entre reserva y savings plan de esa
/// suscripción), nunca de una comparación ad-hoc en la capa de dibujo.</para>
///
/// <para><b>D11: el gap de datos que este contrato no puede cerrar solo.</b> La identidad de un
/// recurso tiene que ser la terna suscripción + grupo de recursos + nombre, igual que en
/// facturación (dos recursos homónimos en suscripciones o grupos distintos no son el mismo
/// recurso). <see cref="AdvisorFila"/> (Recolector, entrega 2a) NO trae <c>resource_group</c>: la
/// tabla <c>waf_resource_finding</c> sí lo tiene (columna existente, usada por
/// <c>MatrizRecolector</c>/otras consultas del módulo WAF) pero <c>AdvisorRecolector.Sql()</c> no
/// lo selecciona hoy. Sin ese campo, <see cref="NumRecursos"/> solo puede identificar por
/// suscripción + nombre, una terna incompleta que sigue colisionando homónimos dentro de la misma
/// suscripción. Se deja documentado como pendiente para quien implemente la Tarea 6: hace falta
/// agregar <c>resource_group</c> a la consulta y al record antes de que D11 se pueda cumplir del
/// todo acá (ver el informe de esta entrega, sección de dudas).</para>
///
/// <para><b>Filas posicionales</b> (arreglos JSON: sobreviven intactas a cualquier política de
/// nombres). <see cref="Suscripciones"/>/<see cref="TiposRecurso"/> = [nombre, cantidad].
/// <see cref="Top"/> = [recomendación, pilar, impacto, recursos]. <see cref="Detalle"/> =
/// [recomendación, pilar, impacto, suscripción, recursos]. <see cref="Pilares"/>, en cambio, es
/// una lista de objetos con nombre (<see cref="PosturaPilar"/>): <c>render()</c> ya lee sus
/// campos por nombre (<c>c.h</c>, <c>c.m</c>, <c>c.l</c>), no por posición.</para>
/// </summary>
public sealed record PosturaModelo(
    [property: JsonPropertyName("n")] int Total,
    [property: JsonPropertyName("tipos_rec")] int TiposDeRecomendacion,
    [property: JsonPropertyName("cats")] IReadOnlyList<PosturaPilar> Pilares,
    [property: JsonPropertyName("subs")] IReadOnlyList<IReadOnlyList<object?>> Suscripciones,
    [property: JsonPropertyName("tipos")] IReadOnlyList<IReadOnlyList<object?>> TiposRecurso,
    [property: JsonPropertyName("top")] IReadOnlyList<IReadOnlyList<object?>> Top,
    [property: JsonPropertyName("topSum")] int TopSuma,
    [property: JsonPropertyName("det")] IReadOnlyList<IReadOnlyList<object?>> Detalle,
    [property: JsonPropertyName("nRes")] int NumRecursos,
    [property: JsonPropertyName("high")] int Alto,
    [property: JsonPropertyName("medium")] int Medio,
    [property: JsonPropertyName("low")] int Bajo,
    [property: JsonPropertyName("bruto")] decimal AhorroBruto,
    [property: JsonPropertyName("real")] decimal AhorroRealizable,
    [property: JsonPropertyName("descarte")] decimal AhorroDescartado,
    [property: JsonPropertyName("nSav")] int ConAhorroCuantificado,
    [property: JsonPropertyName("savLineas")] IReadOnlyList<PosturaLineaAhorro> LineasAhorro,
    [property: JsonPropertyName("porSub")] IReadOnlyDictionary<string, PosturaCompromisoSuscripcion> CompromisoPorSuscripcion,
    [property: JsonPropertyName("rets")] IReadOnlyList<PosturaRetiro> Retiros,
    [property: JsonPropertyName("vencidos")] int RetirosVencidos,
    [property: JsonPropertyName("proximos")] int RetirosProximosATresMeses);

/// <summary>Un pilar Well-Architected con su desglose de impacto (<c>cats</c> de <c>calcAdvisor</c>).</summary>
public sealed record PosturaPilar(
    [property: JsonPropertyName("n")] string Nombre,
    [property: JsonPropertyName("c")] int Cantidad,
    [property: JsonPropertyName("h")] int Alto,
    [property: JsonPropertyName("m")] int Medio,
    [property: JsonPropertyName("l")] int Bajo);

/// <summary>
/// Una línea de la tabla de criterio técnico (<c>savLineas</c> de <c>calcAdvisor</c>).
/// <see cref="Contada"/> es nuevo (D7): true si esta línea entra en <see cref="PosturaModelo.AhorroRealizable"/>
/// (para reserva/savings plan, solo la mayor de las dos por suscripción; el resto sí cuenta
/// siempre), derivado del mismo cálculo que produce el total, nunca de una comparación aparte en
/// la capa de dibujo.
/// </summary>
public sealed record PosturaLineaAhorro(
    [property: JsonPropertyName("rec")] string Recomendacion,
    [property: JsonPropertyName("sub")] string Suscripcion,
    [property: JsonPropertyName("monto")] decimal Monto,
    [property: JsonPropertyName("tipo")] string Tipo,
    [property: JsonPropertyName("contada")] bool Contada);

/// <summary>Compromiso de cómputo evaluado por suscripción (<c>porSub</c> de <c>calcAdvisor</c>):
/// reserva y savings plan no se suman entre sí, se toma el máximo, porque no se pueden comprar los
/// dos sobre el mismo cómputo. <b>Clave de diccionario = nombre de suscripción</b>: es el caso que
/// fija el test de <see cref="InformeValorJsonOptions"/> (una suscripción con espacios y acentos
/// tiene que sobrevivir intacta como clave).</summary>
public sealed record PosturaCompromisoSuscripcion(
    [property: JsonPropertyName("ri")] decimal Reserva,
    [property: JsonPropertyName("sp")] decimal SavingsPlan);

/// <summary>
/// Un retiro de Azure agrupado por anuncio (<c>rets</c> de <c>calcAdvisor</c>), a partir de
/// <see cref="RetiroFila"/>. <see cref="Situacion"/> es la clasificación en prosa que ya hace la
/// plantilla ("VENCIDO...", "Menos de tres meses..."), calculada contra
/// <see cref="ContextoInformeValor.Corte"/> (nunca la hora del sistema, ver Global Constraints
/// del plan). <see cref="Vencido"/> y
/// <see cref="ProximoATresMeses"/> son NUEVOS: la plantilla hoy deriva esas dos condiciones
/// haciendo una expresión regular sobre <see cref="Situacion"/> (<c>/VENCIDO/.test(r.est)</c>,
/// <c>/tres meses/.test(r.est)</c>) para decidir el color de la fila — parsear una oración en
/// español generada por el propio código para recuperar un booleano que ya se conocía al
/// generarla. Estos dos campos evitan ese roundtrip texto→booleano en cualquier vista nueva
/// (React, entrega 3).
/// </summary>
public sealed record PosturaRetiro(
    [property: JsonPropertyName("f")] string Caracteristica,
    [property: JsonPropertyName("d")] string? FechaRetiro,
    [property: JsonPropertyName("c")] int RecursosAfectados,
    [property: JsonPropertyName("est")] string Situacion,
    [property: JsonPropertyName("vencido")] bool Vencido,
    [property: JsonPropertyName("proximoATresMeses")] bool ProximoATresMeses);
