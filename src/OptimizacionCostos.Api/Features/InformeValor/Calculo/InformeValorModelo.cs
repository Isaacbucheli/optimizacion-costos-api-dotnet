using System.Text.Json.Serialization;
using OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// El modelo completo del informe: <see cref="Meta"/> más los cinco bloques —consumo, operación,
/// seguridad, postura y roadmap—, <see cref="CatSerie"/> y <see cref="Ejecutado"/>. Forma exacta
/// del objeto <c>D</c> que arma <c>recalcula()</c> en <c>docs/Plantilla-Dashboard-BIT.html</c>:
/// <c>D.meta</c>, <c>D.tickets</c>, <c>D.fact</c>, <c>D.rbac</c>, <c>D.advisor</c>, <c>D.matriz</c>,
/// <c>D.catSerie</c>, <c>D.ejecutado</c>. Todo bloque ausente en la plantilla es <c>null</c>
/// (insumo no cargado): <c>render()</c> ya maneja ese caso con <c>if(!t){ ...pendiente... }</c>
/// por sección, así que cada propiedad de bloque acá es nullable, nunca un objeto "vacío" que
/// simule ausencia.
///
/// <para><b><see cref="Ejecutado"/> es la octava clave de nivel superior, y va DELIBERADAMENTE
/// fuera de <c>fact</c> (entrega 6, Tarea 6).</b> <c>fact</c> es el bloque de la tabla de
/// hechos —la facturación cruda de BITCOST y lo que de ahí se deriva (variación de consumo,
/// series, comparativas)—, mientras que <see cref="Ejecutado"/> es el titular del informe
/// (decisión 2026-08-13): el acumulado de lo que efectivamente se ejecutó, cruzando tres fuentes
/// que <c>fact</c> nunca mira (el barrido de optimización, la matriz WAF resuelta y las reservas
/// activas). Meterlo dentro de <c>fact</c> habría escondido el dato más importante del informe
/// adentro de un bloque pensado para otra cosa. El contrato de siete claves que fijaba
/// <c>InformeValorJsonOptionsTests</c> se amplía a OCHO a propósito, en el mismo commit que
/// introduce el campo (spec §Modelo): a diferencia de <see cref="InformeValorMeta.Cobertura"/>
/// (D12), que sí vive DENTRO de <c>meta</c> porque <c>render()</c> nunca la va a leer,
/// <see cref="Ejecutado"/> es contenido para el cliente que un renderizador futuro (entrega 7) SÍ
/// va a dibujar — necesita su propio lugar de nivel superior, igual que <c>catSerie</c>, no un
/// rincón de otro bloque.</para>
///
/// <para>Lo ensambla la Tarea 8, que también resuelve D12 (las tres cifras de suscripciones se
/// concilian) sobre este objeto: cruza las suscripciones que ve cada bloque y publica el conjunto
/// unión en <see cref="InformeValorMeta.Cobertura"/>. Vive ahí, DENTRO de <c>meta</c>, y no como
/// una octava clave de nivel superior en <c>D</c>: <c>InformeValorJsonOptionsTests</c> fija que
/// esta forma serializa con exactamente las siete claves que <c>render()</c> espera
/// (<c>meta/tickets/fact/rbac/advisor/matriz/catSerie</c>), y agregar <c>D.cobertura</c> ahí
/// habría roto ese contrato ya probado sin necesidad: <c>render()</c> no lee <c>D.cobertura</c> en
/// ninguna parte (construye su propia sección "Cobertura del servicio" inline, a partir de
/// <c>f</c>/<c>rb</c>/<c>t</c> por separado, sin conciliar — el defecto que D12 corrige), así que
/// esta cifra queda disponible para la vista React de la entrega 3, no para el HTML reusado.
///
/// <para>El resumen ejecutivo, "próximos pasos" y "trazabilidad de las cifras" que arma
/// <c>render()</c> hoy inline SÍ pueden seguir sin campo propio: a diferencia de la cobertura, ahí
/// <c>render()</c> no tiene ningún defecto de conciliación que corregir, así que no hace falta
/// tocar ese cálculo para que siga funcionando igual sobre los cinco bloques ya ensamblados.</para>
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
    [property: JsonPropertyName("catSerie")] IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>>? CatSerie,
    [property: JsonPropertyName("ejecutado")] EjecutadoModelo? Ejecutado = null);

/// <summary>
/// Encabezado del informe (<c>D.meta</c>). <see cref="Corte"/> es la fecha de corte tal como
/// llegó resuelta en <see cref="ContextoInformeValor.Corte"/> (<c>"aaaa-MM-dd"</c>), NO se
/// recalcula acá: es la misma restricción de las Global Constraints, un valor congelado en el
/// momento de generar, no un reloj. <see cref="Periodo"/> es texto libre en la plantilla (el
/// consultor lo escribe a mano y no se contrasta contra nada); en este módulo puede seguir siendo
/// una etiqueta descriptiva del rango, porque el rango real que SÍ filtra los datos
/// (<see cref="ContextoInformeValor.PeriodStart"/>/<see cref="ContextoInformeValor.PeriodEnd"/>,
/// D0) vive en la entrega de la base de datos, no en este texto.
///
/// <para><see cref="RbacOrigen"/> es <see cref="Recolector.InsumosBd.RbacOrigen"/> tal cual lo
/// resolvió el recolector: mismo caso que <see cref="Cobertura"/> (campo que <c>render()</c> no
/// lee, pensado para la vista React de la entrega 3 y para la bitácora de la entrega, spec
/// <c>informe_valor_entrega.rbac_origen</c>). Un consultor que no puede saber si el bloque de
/// seguridad salió de la base o del Excel de respaldo no puede explicar sus cifras.</para>
///
/// <para><see cref="Conciliacion"/> es la Tarea 8 de la entrega 6: vive DENTRO de <c>meta</c> por el
/// mismo motivo que <see cref="Cobertura"/> — ningún bloque de <see cref="ModeloInformeValor"/> ve a
/// la vez la tabla de hechos y el archivo de evolución, así que el cruce lo arma
/// <see cref="InformeValorEnsamblador"/> y no tiene otro lugar donde publicarse. Ningún renderizador
/// la dibuja todavía (entrega 7 la toma); <c>null</c> cuando no hay evolución cargada, mismo caso
/// que un bloque ausente en cualquier otra parte del modelo.</para>
/// </summary>
public sealed record InformeValorMeta(
    [property: JsonPropertyName("cliente")] string Cliente,
    [property: JsonPropertyName("periodo")] string Periodo,
    [property: JsonPropertyName("corte")] string Corte,
    [property: JsonPropertyName("cobertura")] InformeValorCobertura Cobertura,
    [property: JsonPropertyName("rbacOrigen")] string? RbacOrigen = null,
    [property: JsonPropertyName("conciliacion")] ConciliacionArchivos? Conciliacion = null);

/// <summary>
/// D12 (Tarea 8): las tres cifras de suscripciones del informe —de facturación, de RBAC y de
/// Advisor— se concilian normalizando por <c>subscription_id</c> donde exista (por nombre solo
/// cuando ninguna fila de ninguna de las tres fuentes trae id para esa suscripción) y publicando
/// el conjunto UNIÓN, no la intersección: una suscripción que solo aparece en una fuente sigue
/// contando, con esa única fuente marcada. La matriz (<see cref="RoadmapModelo"/>) no participa:
/// <c>MatrizFila</c> no tiene columna de suscripción.
/// </summary>
public sealed record InformeValorCobertura(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("suscripciones")] IReadOnlyList<CoberturaSuscripcion> Suscripciones);

/// <summary>Una suscripción del conjunto unión de <see cref="InformeValorCobertura"/>, con qué
/// fuente la vio. <see cref="Nombre"/> es el mejor nombre visto en cualquiera de las tres fuentes;
/// si ninguna trajo nombre para este id, <see cref="Nombre"/> es el propio <see cref="Id"/> (nunca
/// se pierde la fila por falta de nombre).</summary>
public sealed record CoberturaSuscripcion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("nombre")] string Nombre,
    [property: JsonPropertyName("facturacion")] bool Facturacion,
    [property: JsonPropertyName("rbac")] bool Rbac,
    [property: JsonPropertyName("advisor")] bool Advisor);
