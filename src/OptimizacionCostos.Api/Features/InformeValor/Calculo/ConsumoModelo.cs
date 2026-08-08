using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Bloque de consumo: facturación (insumo BITCOST). Nombres sacados de <c>calcFact</c> en
/// <c>docs/Plantilla-Dashboard-BIT.html</c>; <c>D.fact</c> en el modelo embebido. Contrato de la
/// Tarea 2 del plan de la entrega 2b: la Tarea 3 (D0, D3, D4, D5, D6, D14) implementa el cálculo.
///
/// <para><b>D4: una sola cifra de carga retirada.</b> La plantilla publica <c>cargaRet</c> y
/// <c>cargaAcum</c> en tarjetas contiguas —dos definiciones distintas del mismo concepto, con el
/// rótulo de una describiendo la fórmula de la otra— y <c>cargaAcum</c> además suma tasas
/// mensuales de meses distintos, que no es ni un monto ni una tasa. La regla nueva publica
/// <b>una sola</b>: <see cref="CargaRetirada"/>, la suma —una vez por recurso— del importe de su
/// último mes facturado, entre los recursos que dejaron de facturar dentro del rango.
/// <c>cargaAcum</c> no tiene equivalente en este contrato: desaparece, no se renombra.
/// <see cref="UnidadCargaRetirada"/> existe para que la unidad quede declarada como dato, no solo
/// como prosa de <c>render()</c>.</para>
///
/// <para><b>D3: el ahorro sostenido se rehace</b> (ver <see cref="ConsumoAhorro"/>).</para>
///
/// <para><b>D5/D6 son decisiones de dibujo y de rótulo, no de forma del modelo.</b> D5 (barras
/// agrupadas, nunca apiladas, y las bajas de un mes parcial se excluyen del CONTEO) y D6
/// ("desconexiones del mes" para las transiciones de <see cref="Serie"/> contra "bajas
/// definitivas" para <see cref="BajasDefinitivas"/>) no agregan ni quitan campos de este
/// contrato: la Tarea 3 tiene que asegurarse de que el CONTEO de bajas dentro de
/// <see cref="Serie"/> ya excluya los meses parciales antes de publicarlo, no dejarlo para
/// <c>render()</c>.</para>
///
/// <para><b>D14: <see cref="Filas"/> es el conteo histórico, antes de fusionar.</b> La plantilla
/// publica "revisado línea por línea sobre N registros" con el N de las filas aceptadas ANTES de
/// fusionar por clave natural. La Tarea 3 lo lee de <c>informe_valor_ingesta</c>
/// (<c>rows_processed + rows_merged</c>, columna agregada en la Tarea 1 de esta entrega), no lo
/// recalcula contando filas de <c>informe_valor_facturacion</c> (que ya están fusionadas).</para>
///
/// <para><b>Filas posicionales</b> (arreglos JSON, no objetos: sobreviven intactas a cualquier
/// política de nombres, y <c>render()</c> ya las lee por posición). <see cref="SerieMensual"/> =
/// [mes "aaaa-MM", monto redondeado, 1 si es parcial / 0 si no]. <see cref="Suscripciones"/> =
/// [nombre de suscripción, monto]. <see cref="Serie"/> = [mes, recursos activos, altas, bajas del
/// mes (ya sin las de un mes parcial, D5), monto del mes, monto retirado del mes, 1 si es parcial
/// / 0 si no]. <see cref="PromediosPorAnio"/> = [año, meses completos de ese año, promedio
/// mensual, total anual]. <see cref="PorCentroCosto"/> = [centro de costo, monto].</para>
/// </summary>
public sealed record ConsumoModelo(
    [property: JsonPropertyName("filas")] int Filas,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("meses")] IReadOnlyList<IReadOnlyList<object?>> SerieMensual,
    [property: JsonPropertyName("ultCompleto")] string? UltimoMesCompleto,
    [property: JsonPropertyName("parciales")] IReadOnlyList<string> MesesParciales,
    [property: JsonPropertyName("autoParciales")] IReadOnlyList<string> MesesParcialesDetectadosAuto,
    [property: JsonPropertyName("subs")] IReadOnlyList<IReadOnlyList<object?>> Suscripciones,
    [property: JsonPropertyName("nRecursos")] int NumRecursos,
    [property: JsonPropertyName("nIds")] int NumIdentidades,
    [property: JsonPropertyName("nRg")] int NumGruposRecursos,
    [property: JsonPropertyName("nCats")] int NumCategorias,
    [property: JsonPropertyName("picoAct")] int PicoRecursosActivos,
    [property: JsonPropertyName("picoMes")] string? MesDePicoActivos,
    [property: JsonPropertyName("serie")] IReadOnlyList<IReadOnlyList<object?>> Serie,
    [property: JsonPropertyName("bajasDef")] int BajasDefinitivas,
    [property: JsonPropertyName("cargaRet")] decimal CargaRetirada,
    [property: JsonPropertyName("unidadCargaRet")] string UnidadCargaRetirada,
    [property: JsonPropertyName("prom")] IReadOnlyList<IReadOnlyList<object?>> PromediosPorAnio,
    [property: JsonPropertyName("ahorro")] ConsumoAhorro? Ahorro,
    [property: JsonPropertyName("comp")] ConsumoComparativa? Comparativa,
    [property: JsonPropertyName("cc")] IReadOnlyList<IReadOnlyList<object?>> PorCentroCosto);

/// <summary>
/// Ahorro sostenido en la factura, rehecho por D3: tres defectos encadenados en la plantilla —el
/// pico se inicializa en cero (una categoría con solo notas de crédito "ahorra"), el pico es el
/// valor de UN mes y no una línea base (la volatilidad normal garantiza una diferencia positiva),
/// y esa diferencia se anualiza sin condición. Regla nueva:
/// <see cref="LineaBase"/> es la MEDIANA de los meses anteriores al quiebre (no el máximo: el nombre y
/// la forma del campo se conservan por compatibilidad con <c>render()</c>, que lee <c>pico</c>,
/// pero el valor ya no es un pico). Se exige base positiva y <see cref="Fin"/> no negativo antes
/// de evaluar nada. <see cref="TasaMensual"/> es la cifra publicada como tasa MENSUAL observada;
/// <see cref="Anualizada"/> solo tiene valor cuando <see cref="MesesSostenido"/> ≥ 3 (meses
/// cerrados consecutivos con la caída sostenida) — si no, es <c>null</c> y no se publica ninguna
/// cifra anualizada, ni siquiera implícita. Las categorías que subieron se netean contra las que
/// bajaron antes de elegir la mayor caída: no puede haber, en la misma sección, un titular de
/// "creció por adopción" y un ahorro activo publicado a la vez sobre otra categoría no neteada.
/// </summary>
public sealed record ConsumoAhorro(
    [property: JsonPropertyName("cat")] string Categoria,
    [property: JsonPropertyName("pico")] decimal LineaBase,
    [property: JsonPropertyName("picoMes")] string BaseDesdeMes,
    [property: JsonPropertyName("fin")] decimal Fin,
    [property: JsonPropertyName("finMes")] string FinHastaMes,
    [property: JsonPropertyName("dif")] decimal TasaMensual,
    [property: JsonPropertyName("mesesSostenido")] int MesesSostenido,
    [property: JsonPropertyName("anualizada")] decimal? Anualizada);

/// <summary>Comparativa interanual del mismo mes de calendario (<c>comp</c> de <c>calcFact</c>).</summary>
public sealed record ConsumoComparativa(
    [property: JsonPropertyName("a")] string MesBase,
    [property: JsonPropertyName("b")] string MesComparado,
    // Filas = [servicio, monto del mes base, monto del mes comparado].
    [property: JsonPropertyName("filas")] IReadOnlyList<IReadOnlyList<object?>> Filas);
