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
/// <para><b>D14: dos cifras de "filas", cada una rotulada por lo que cuenta.</b> La plantilla
/// publica "revisado línea por línea sobre N registros" con el N de las filas aceptadas ANTES de
/// fusionar por clave natural, del archivo COMPLETO (la plantilla nunca filtra por período, D0). En
/// C# eso ya no es una sola cifra honesta: D0 filtra por rango, y el conteo de fusionadas
/// (<c>rows_merged</c>) no se puede partir por mes, así que no hay forma de reconstruir "cuántas
/// filas, antes de fusionar, cayeron en este rango". Publicar solo el número de la bitácora (todo
/// el archivo) en un informe de un rango corto exagera cuánto se revisó PARA ESE PERÍODO; publicar
/// solo el conteo en rango subestima cuánto se revisó en total, porque ya está fusionado. Las dos
/// cifras son ciertas y ninguna sola alcanza, así que se publican las dos, cada una con su propio
/// nombre: <see cref="Filas"/> (toda la carga, como antes) y <see cref="FilasEnRango"/> (nuevo).
/// </para>
///
/// <para><b>El aviso de mes forzado inexistente (spec §12.3.3) tiene campo propio.</b>
/// <see cref="MesesParcialesInexistentes"/> publica los meses de
/// <see cref="ContextoInformeValor.MesesParcialesForzados"/> que el consultor declaró pero que no
/// existen en el insumo filtrado: antes se ignoraban en silencio (igual que <c>calcFact</c>), y
/// ese silencio es justo lo que este módulo viene a eliminar. Vacío cuando no hay ninguno, no
/// cuando el campo no se calculó.</para>
///
/// <para><b>Filas posicionales</b> (arreglos JSON, no objetos: sobreviven intactas a cualquier
/// política de nombres, y <c>render()</c> ya las lee por posición). <see cref="SerieMensual"/> =
/// [mes "aaaa-MM", monto redondeado, 1 si es parcial / 0 si no]. <see cref="Suscripciones"/> =
/// [nombre de suscripción, monto]. <see cref="Serie"/> = [mes, recursos activos, altas, bajas del
/// mes (ya sin las de un mes parcial, D5), monto del mes, monto retirado del mes, 1 si es parcial
/// / 0 si no]. <see cref="PromediosPorAnio"/> = [año, meses completos de ese año, promedio
/// mensual, total anual]. <see cref="PorCentroCosto"/> = [centro de costo, monto].</para>
///
/// <para><b>Tarea 7 de la entrega 6, dos filas posicionales más, las dos derivadas de agregados
/// que <see cref="Calculo.ConsumoCalculador.Calcular"/> ya arma para otro campo (no se vuelve a
/// agrupar la facturación).</b> <see cref="CostoUnitario"/> = [mes, recursos activos, monto del
/// mes, costo por recurso (monto ÷ recursos, redondeado; <c>null</c> si ese mes no tuvo recursos
/// activos, para no dividir por cero), 1 si es parcial / 0 si no] — recursos y monto son los
/// mismos índices 1 y 4 de <see cref="Serie"/>, releídos, no recalculados. Es el argumento del
/// HTML de referencia: recursos activos suben, factura sube menos, costo por recurso baja.
/// <see cref="VariacionMoM"/> = [mes, reducciones (positivo), incrementos (positivo), neto =
/// reducciones − incrementos, 1 si el mes es parcial / 0 si no], una fila por cada mes del rango
/// salvo el primero (que no tiene mes anterior contra el cual comparar). Por categoría, compara
/// el monto de ese mes contra el anterior dentro del rango — una categoría ausente en un mes
/// cuenta como cero ese mes — y suma las caídas por un lado y las subidas por otro, ya en
/// positivo. Observación 6 de la reunión: el dibujo (entrega 7) pone las reducciones arriba del
/// eje y los incrementos abajo; publicar las dos series ya separadas es lo que hace posible ese
/// dibujo sin que el modelo tenga que saber de ejes. El flag de mes parcial (índice 4, defecto del
/// plan original, corregido en el review final de la entrega 6, I4) sigue la misma convención que
/// <see cref="SerieMensual"/>/<see cref="Serie"/>/<see cref="CostoUnitario"/>: sin él, esta era la
/// única fila posicional de la entrega que no declaraba si su propio mes era parcial.</para>
/// </summary>
public sealed record ConsumoModelo(
    /// <summary>Filas aceptadas antes de fusionar, de TODA la carga (D14, sin filtrar por rango:
    /// ver el docstring de la clase). No es reconstruible por período porque <c>rows_merged</c> no
    /// se persiste por mes.</summary>
    [property: JsonPropertyName("filas")] int Filas,
    /// <summary>Filas de facturación (ya fusionadas) que cayeron dentro de
    /// <see cref="ContextoInformeValor.PeriodStart"/>/<see cref="ContextoInformeValor.PeriodEnd"/>
    /// (D0). Subestima la revisión real de ese período porque ya está fusionado, pero es
    /// reconciliable: cualquiera puede volver a contar las filas de ese rango y llegar al mismo
    /// número. Contraparte de <see cref="Filas"/>, no un reemplazo.</summary>
    [property: JsonPropertyName("filasEnRango")] int FilasEnRango,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("meses")] IReadOnlyList<IReadOnlyList<object?>> SerieMensual,
    [property: JsonPropertyName("ultCompleto")] string? UltimoMesCompleto,
    [property: JsonPropertyName("parciales")] IReadOnlyList<string> MesesParciales,
    [property: JsonPropertyName("autoParciales")] IReadOnlyList<string> MesesParcialesDetectadosAuto,
    [property: JsonPropertyName("parcialesInexistentes")] IReadOnlyList<string> MesesParcialesInexistentes,
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
    [property: JsonPropertyName("cc")] IReadOnlyList<IReadOnlyList<object?>> PorCentroCosto,
    [property: JsonPropertyName("unitario")] IReadOnlyList<IReadOnlyList<object?>> CostoUnitario,
    [property: JsonPropertyName("mom")] IReadOnlyList<IReadOnlyList<object?>> VariacionMoM,
    /// <summary>
    /// Tarea 5 de la entrega 2d (E0): la descomposicion de a donde fue la variacion del consumo —
    /// nunca "ahorro" (ver <see cref="VariacionConsumoModelo"/>). Sibling de <see cref="Ahorro"/>
    /// (D3, entrega 2b), no su reemplazo: D3 sigue siendo el titular robusto por mediana de UNA
    /// categoria (E1, "eso se queda"); esto es la atribucion completa del portafolio con promedios,
    /// que si se puede sumar y cerrar al centavo. Puesto DENTRO de <c>fact</c> (no como octava clave
    /// de <c>D</c>) por el mismo motivo que <see cref="InformeValorMeta.Cobertura"/> vive dentro de
    /// <c>meta</c>: <c>render()</c> no conoce esta sección (es enteramente nueva, no un port), y
    /// agregarla como octava clave de nivel superior habria roto el contrato ya probado de las
    /// siete claves exactas que espera el HTML reusado (ver <c>InformeValorJsonOptionsTests</c>).
    /// Null cuando la ventana fija no alcanza (menos de seis meses no parciales en el rango del
    /// informe): el ensamblador deja este campo en null en vez de publicar una descomposicion sobre
    /// una ventana demasiado corta para significar algo, mismo criterio que ya usa
    /// <see cref="Ahorro"/> (D3) y <c>AtribucionCalculador</c>.
    /// </summary>
    [property: JsonPropertyName("variacionConsumo")] VariacionConsumoModelo? VariacionConsumo = null);

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

/// <summary>
/// Tarea 5 de la entrega 2d (E0, ensamblado): los tres baldes de la atribucion, ya sumados. E0: "la
/// sección deja de llamarse ahorro y pasa a ser variación del consumo, y su titular es el monto que
/// se movió, no un logro." <see cref="VariacionTotal"/> es ese titular.
///
/// <para><b><see cref="Reservas"/> siempre viaja</b> (balde 1, con sus dos mediciones — desde el
/// propio inicio de cada reserva para el panel de reservas, y <see cref="AhorroReservasModelo.AporteAlPeriodo"/>
/// para esta sección — ver el comentario de clase de <see cref="AhorroReservasCalculador"/>, E9),
/// AUNQUE <see cref="Atribucion"/> sea null: el panel de cobertura de reservas (E5) no depende de
/// que haya seis meses de historia de facturación, solo de que haya reservas que leer.</para>
///
/// <para><b><see cref="Atribucion"/> y <see cref="VariacionTotal"/> viajan juntos.</b> Los baldes 2 y
/// 3 (<see cref="AtribucionModelo.PorRecomendacion"/>/<see cref="AtribucionModelo.SinAtribuir"/>) sí
/// necesitan la ventana fija completa (mínimo seis meses no parciales): sin eso,
/// <c>AtribucionCalculador.Calcular</c> devuelve <c>null</c>, y sin baldes 2 y 3 no hay una
/// descomposición completa que totalizar, así que los dos campos son null a la vez — nunca un
/// <see cref="VariacionTotal"/> que solo cuenta el balde de reservas disfrazado de total completo.
/// </para>
///
/// <para><b>Por qué <see cref="VariacionTotal"/> no es <see cref="AtribucionModelo.VariacionTotal"/>.</b>
/// El de <see cref="AtribucionModelo"/> es la suma de SOLO los baldes 2 y 3 (documentado ahí: "quien
/// ensambla el informe le suma el balde de reserva para llegar a la variación total del consumo
/// completo"). Este es esa suma completa: <see cref="Reservas"/>.AporteAlPeriodo +
/// <see cref="Atribucion"/>.PorRecomendacion.Total + <see cref="Atribucion"/>.SinAtribuir.Total, sin
/// volver a redondear (los tres sumandos ya están redondeados una vez cada uno — E1 — y la suma de
/// tres <see cref="decimal"/> exactos de dos cifras da otro exacto de dos cifras).</para>
/// </summary>
public sealed record VariacionConsumoModelo(
    [property: JsonPropertyName("reservas")] AhorroReservasModelo Reservas,
    [property: JsonPropertyName("atribucion")] AtribucionModelo? Atribucion,
    [property: JsonPropertyName("variacionTotal")] decimal? VariacionTotal);
