using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Bloque de atribución (Tareas 3 y 4 del plan de la entrega 2d, E1/E3/E4/E6): de la variación total
/// del consumo, cuánto se puede probar que vino de una recomendación resuelta de la matriz
/// (<see cref="PorRecomendacion"/>, balde 2) y, de lo que queda, cómo se abre por mecanismo
/// (<see cref="SinAtribuir"/>, balde 3). El balde 1 (por reserva, E2) no se calcula acá: lo
/// construye otra tarea de la misma entrega. Ver <see cref="AtribucionCalculador"/> para el cálculo.
///
/// <para><b>Convención de signo, la misma que <see cref="ConsumoAhorro"/> (D3): positivo = el gasto
/// bajó, negativo = el gasto subió.</b> Cada <see cref="AtribucionBalde.Total"/> es
/// <c>promedio(base) − promedio(fin)</c> sumado recurso a recurso. Para <see cref="SinAtribuirModelo.VivoCuestaMas"/>
/// y <see cref="SinAtribuirModelo.Nuevo"/> eso da un número NEGATIVO casi siempre (ahí el gasto subió,
/// no bajó): no es un error de signo, es la misma regla aplicada sin excepción para que la suma
/// de todos los baldes cierre con una simple adición, nunca con una resta especial para el
/// crecimiento. <see cref="Crecimiento"/> existe aparte, en magnitud POSITIVA ("cuánto más se está
/// gastando por estos dos mecanismos"), para que el número que lee un consultor no tenga que
/// interpretarse con el signo invertido.</para>
///
/// <para><b>E1 — por qué la suma cierra al centavo, siempre, no solo en los casos de prueba.</b> Cada
/// <see cref="AtribucionBalde.Total"/> se redondea UNA sola vez, con <see cref="Redondeo.ComoJs"/>,
/// a partir de la suma SIN redondear de los deltas por recurso que caen en ese balde. Todo total de
/// nivel superior (<see cref="SinAtribuirModelo.Total"/>, <see cref="VariacionTotal"/>) es la SUMA de
/// esos baldes YA redondeados, nunca una cifra redonda de forma independiente a partir de los deltas
/// crudos: sumar decimales de 2 cifras siempre da otro decimal exacto de 2 cifras en
/// <see cref="decimal"/> (sin el error de redondeo en punto flotante), así que la igualdad
/// <c>PorRecomendacion.Total + SinAtribuir.Total == VariacionTotal</c> es una identidad aritmética,
/// no una casualidad de los números elegidos para el test. Es la lección de la Parte 1 del plan: con
/// medianas la suma de las partes no da el total (no son lineales); con promedios sí, pero solo si
/// además se evita redondear cada parte por separado antes de sumarla al total.</para>
///
/// <para><b>Recursos excluidos por reserva (E3, el punto de encuentro con la Tarea 1/2 de esta misma
/// entrega).</b> <see cref="AtribucionCalculador.Calcular"/> recibe el conjunto de recursos que la
/// tarea de reservas ya confirmó cubiertos (mismo formato de id que usa internamente: ver el
/// comentario de esa clase) y los saca de <see cref="PorRecomendacion"/> y de
/// <see cref="SinAtribuir"/> ANTES de clasificar nada: "gana la reserva" (E3) porque el efecto de la
/// reserva se puede medir por separado y el de la recomendación no, así que atribuirle el total a la
/// recomendación cuando las dos aplican inflaría la autoría. <see cref="ExcluidosPorReserva"/> deja
/// esos recursos anotados (con su delta, calculado igual que cualquier otro) para que quien ensambla
/// el informe pueda conciliar cuánto de la variación total se movió al balde de reserva.</para>
/// </summary>
public sealed record AtribucionModelo(
    [property: JsonPropertyName("porRecomendacion")] AtribucionBalde PorRecomendacion,
    [property: JsonPropertyName("sinAtribuir")] SinAtribuirModelo SinAtribuir,
    /// <summary>Magnitud POSITIVA de <see cref="SinAtribuirModelo.VivoCuestaMas"/> +
    /// <see cref="SinAtribuirModelo.Nuevo"/> (que son negativos bajo la convención de signo de la
    /// clase): "cuánto más se está gastando", para lectura directa sin invertir el signo mentalmente.
    /// </summary>
    [property: JsonPropertyName("crecimiento")] decimal Crecimiento,
    /// <summary><see cref="PorRecomendacion"/>.Total + <see cref="SinAtribuir"/>.Total, exacto (ver
    /// el comentario de clase, E1). Es la variación de TODOS los recursos que no quedaron en
    /// <see cref="ExcluidosPorReserva"/>: quien ensambla el informe le suma el balde de reserva para
    /// llegar a la variación total del consumo completo.</summary>
    [property: JsonPropertyName("variacionTotal")] decimal VariacionTotal,
    [property: JsonPropertyName("excluidosPorReserva")] IReadOnlyList<AtribucionRecurso> ExcluidosPorReserva);

/// <summary>
/// Balde 3 (E4): lo que ni la reserva ni una recomendación resuelta explican, desglosado en los
/// cuatro mecanismos que el plan nombra. <b>El desglose no es opcional</b> (E4): publicar solo
/// <see cref="Total"/> sin sus cuatro partes es exactamente el balde "otros" sin abrir que el plan
/// prohíbe. Los cuatro son mutuamente excluyentes y cubren TODO recurso que no esté en
/// <see cref="AtribucionModelo.PorRecomendacion"/> ni en <see cref="AtribucionModelo.ExcluidosPorReserva"/>:
/// ningún recurso con delta distinto de cero en el período queda fuera de los cuatro.
/// </summary>
public sealed record SinAtribuirModelo(
    /// <summary>Recurso que facturaba en la ventana base y dejó de facturar en la ventana de cierre
    /// (promedio de cierre = 0). Puede incluir recursos que en realidad se dieron de baja por una
    /// limpieza deliberada o porque el proyecto que los usaba terminó: no hay evidencia en la
    /// plataforma para decidir cuál de las dos, por eso cae acá y no en <c>PorRecomendacion</c>.
    /// </summary>
    [property: JsonPropertyName("dejoDeFacturar")] AtribucionBalde DejoDeFacturar,
    /// <summary>Recurso con promedio &gt; 0 en las dos ventanas, y el de cierre es menor (o igual: un
    /// empate exacto no "subió", así que por exclusión cae acá — ver <see cref="AtribucionCalculador"/>).
    /// </summary>
    [property: JsonPropertyName("vivoCuestaMenos")] AtribucionBalde VivoCuestaMenos,
    /// <summary>Recurso con promedio &gt; 0 en las dos ventanas, y el de cierre es estrictamente
    /// mayor. Total negativo bajo la convención de signo de la clase (ver el comentario de
    /// <see cref="AtribucionModelo"/>).</summary>
    [property: JsonPropertyName("vivoCuestaMas")] AtribucionBalde VivoCuestaMas,
    /// <summary>Recurso sin facturación en la ventana base (promedio = 0) que aparece facturando en
    /// la ventana de cierre. Total negativo, mismo motivo que <see cref="VivoCuestaMas"/>.</summary>
    [property: JsonPropertyName("nuevo")] AtribucionBalde Nuevo,
    /// <summary>Suma de los cuatro <c>Total</c> ya redondeados (E1): headline del balde 3.</summary>
    [property: JsonPropertyName("total")] decimal Total);

/// <summary>
/// Un balde con nombre (recomendación, o uno de los cuatro mecanismos de <see cref="SinAtribuirModelo"/>):
/// su <see cref="Total"/> (ya redondeado, ver E1) y los recursos que lo componen, para que el
/// consultor pueda defender la cifra señalando cada recurso, no solo el agregado.
/// </summary>
public sealed record AtribucionBalde(
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("cantidad")] int Cantidad,
    [property: JsonPropertyName("recursos")] IReadOnlyList<AtribucionRecurso> Recursos);

/// <summary>
/// Un recurso dentro de un <see cref="AtribucionBalde"/> (o de <see cref="AtribucionModelo.ExcluidosPorReserva"/>).
/// <see cref="SubscriptionId"/>/<see cref="ResourceGroup"/>/<see cref="ResourceName"/> son la misma
/// terna de identidad que usa todo el módulo (D11/E6): <see cref="SubscriptionId"/> ya lleva el
/// mismo respaldo a <see cref="SubscriptionName"/> que usa <c>ConsumoCalculador</c> cuando la fila de
/// facturación no trae id. <see cref="BaseAvg"/>/<see cref="FinAvg"/>/<see cref="Delta"/> viajan
/// redondeados de forma independiente, solo para mostrarse: su suma puede diferir en algún centavo
/// de <see cref="AtribucionBalde.Total"/> (que se calcula sobre los valores SIN redondear, ver E1) —
/// no es la cifra que tiene que cerrar la invariante, es el desglose fila por fila.
/// <see cref="Recomendaciones"/> solo se llena para los recursos de <c>PorRecomendacion</c> (qué
/// hallazgo(s) resuelto(s) lo justifican); vacía en los cuatro mecanismos de <see cref="SinAtribuirModelo"/>.
/// </summary>
public sealed record AtribucionRecurso(
    [property: JsonPropertyName("subscriptionId")] string SubscriptionId,
    [property: JsonPropertyName("subscriptionName")] string SubscriptionName,
    [property: JsonPropertyName("resourceGroup")] string ResourceGroup,
    [property: JsonPropertyName("resourceName")] string ResourceName,
    [property: JsonPropertyName("baseAvg")] decimal BaseAvg,
    [property: JsonPropertyName("finAvg")] decimal FinAvg,
    [property: JsonPropertyName("delta")] decimal Delta,
    [property: JsonPropertyName("recomendaciones")] IReadOnlyList<string> Recomendaciones);
