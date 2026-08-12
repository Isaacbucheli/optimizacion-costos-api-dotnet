using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Resultado de la Tarea 2 del plan de la entrega 2d (E2, E5): el ahorro atribuible a reservas,
/// calculado sobre <see cref="Recolector.FotoReservas"/> (Tarea 1). Ver
/// <see cref="AhorroReservasCalculador"/> para el algoritmo.
///
/// <para><b><see cref="Medido"/> se propaga desde la foto.</b> Si
/// <see cref="Recolector.FotoReservas.Medido"/> es <c>false</c> (sin credenciales, o con errores de
/// lectura), este modelo tambien lo es: publicar <see cref="AhorroConfirmado"/> en cero cuando el
/// eje no se pudo leer diria "no hay ahorro por reservas" en vez de "no se sabe" — la misma
/// distincion de D9 que ya trae la foto.</para>
///
/// <para><see cref="Confirmados"/> incluye TODOS los pares reserva-consumidor confirmados, incluso
/// los que no se pudieron calcular (<see cref="AhorroPorRecurso.Ahorro"/> nulo, con
/// <see cref="AhorroPorRecurso.MotivoSinCalcular"/>) o que resultaron en una discrepancia: no se
/// descarta ninguno en silencio. <see cref="AhorroConfirmado"/> suma solo los que si se pudieron
/// calcular.</para>
///
/// <para><see cref="Estimados"/> son unidades reservadas sin un consumidor confirmado (E2): no
/// tienen terna propia, asi que no se cruzan contra facturacion ni suman a
/// <see cref="AhorroConfirmado"/> — se publican aparte, rotuladas, para que no se confundan con la
/// cifra confirmada.</para>
///
/// <para><see cref="Discrepancias"/> (E2, "la facturacion sirve para contrastar, no para decidir"):
/// un recurso con cobertura confirmada por la app cuya facturacion no muestra una baja de tarifa
/// despues del inicio de la reserva. El informe no elige la causa (reserva vencida sin marcar,
/// export incompleto, etc.): solo la publica.</para>
///
/// <para><b>E9 (entrega 2d, tarea 5): la fecha de la reserva atribuye, la ventana del informe mide.</b>
/// <see cref="AhorroConfirmado"/>/<see cref="AhorroPorRecurso.Ahorro"/> (arriba) miden desde el
/// PROPIO inicio de cada reserva: son la cifra correcta para el panel de reservas, pero no se pueden
/// sumar con los baldes 2 y 3 (<see cref="AtribucionCalculador"/>), que miden sobre una ventana FIJA
/// para todo el portafolio — sumar mediciones tomadas sobre ventanas distintas y llamar al resultado
/// "la variacion del periodo" seria aritmeticamente posible y semanticamente falso. Por eso este
/// modelo tambien expone <see cref="AporteAlPeriodo"/>: la MISMA idea de ahorro por reserva, pero
/// medida sobre la ventana fija del informe (ver <see cref="AhorroPorRecurso.ExplicaElPeriodo"/> y
/// <see cref="AhorroPorRecurso.AporteAlPeriodo"/>), lista para sumarse a los otros dos baldes.
/// <see cref="RecursosQueExplicanElPeriodo"/> es el conjunto que hay que excluir de esos otros dos
/// baldes para que "gana la reserva" (E3) no se aplique tambien a una reserva que no explica nada
/// de lo que paso DENTRO del periodo.</para>
///
/// <para><b>Los nombres JSON son explicitos, en camelCase, igual que el resto del modulo</b> (ver
/// <see cref="AtribucionModelo"/>, que es su hermano dentro de <see cref="VariacionConsumoModelo"/>).
/// Sin <c>[JsonPropertyName]</c> este bloque salia con DOS grafias distintas segun por donde se
/// serializara: snake_case por la politica global de <c>Program.cs</c> cuando lo devuelve un
/// controller, y PascalCase bajo <see cref="InformeValorJsonOptions"/> (que tiene
/// <c>PropertyNamingPolicy = null</c> y es el que el artefacto HTML de la entrega 3 tiene que usar si
/// o si). Dos nombres para el mismo campo obligarian a la vista a deserializar la misma clave de dos
/// formas, y ademas rompian de frente la invariante que <see cref="InformeValorJsonOptions"/>
/// documenta como su razon de existir: cada campo lleva su nombre exacto en el modelo, nunca se
/// infiere.</para>
/// </summary>
public sealed record AhorroReservasModelo(
    [property: JsonPropertyName("medido")] bool Medido,
    [property: JsonPropertyName("motivo")] string Motivo,
    [property: JsonPropertyName("errores")] IReadOnlyList<object> Errores,
    [property: JsonPropertyName("alertDays")] int AlertDays,
    /// <summary>Suma de <see cref="AhorroPorRecurso.Ahorro"/> entre los <see cref="Confirmados"/>
    /// que si se pudieron calcular. Cero cuando <see cref="Medido"/> es <c>false</c> — no es que no
    /// haya ahorro, es que no se midio (ver <see cref="Medido"/>). Subestimado cuando
    /// <see cref="ReservasConConsumidoresNoLeidos"/> es mayor a cero.</summary>
    [property: JsonPropertyName("ahorroConfirmado")] decimal AhorroConfirmado,
    [property: JsonPropertyName("confirmados")] IReadOnlyList<AhorroPorRecurso> Confirmados,
    [property: JsonPropertyName("estimados")] IReadOnlyList<EstimadoPorReserva> Estimados,
    [property: JsonPropertyName("discrepancias")] IReadOnlyList<DiscrepanciaCobertura> Discrepancias,
    /// <summary>
    /// Balde 1 de la atribucion (E9), listo para sumarse con los baldes 2 y 3
    /// (<see cref="AtribucionModelo.PorRecomendacion"/>.Total + <see cref="AtribucionModelo.SinAtribuir"/>.Total):
    /// suma de <see cref="AhorroPorRecurso.AporteAlPeriodo"/> entre los recursos con
    /// <see cref="AhorroPorRecurso.ExplicaElPeriodo"/> en <c>true</c>, deduplicados por terna (un
    /// mismo recurso confirmado bajo dos reservas elegibles no se cuenta dos veces), redondeada UNA
    /// sola vez desde la suma SIN redondear (mismo criterio de E1 que ya usa
    /// <see cref="AtribucionCalculador"/>: ver su comentario de clase). Cero cuando <see cref="Medido"/>
    /// es <c>false</c> o cuando la ventana fija no se pudo calcular (menos de seis meses no
    /// parciales en el rango del informe) — en los dos casos no hay nada que sumar, no que el aporte
    /// haya sido cero.
    /// </summary>
    [property: JsonPropertyName("aporteAlPeriodo")] decimal AporteAlPeriodo,
    /// <summary>
    /// La terna (mismo formato D11/E6 que usa <see cref="AtribucionCalculador"/>: <c>subscriptionId
    /// + "|" + resourceGroup + "|" + resourceName</c>, SIN normalizar mayusculas/minusculas) de cada
    /// recurso confirmado cuya reserva SI explica algo dentro del periodo del informe
    /// (<see cref="AhorroPorRecurso.ExplicaElPeriodo"/> en <c>true</c>). Quien ensambla el informe
    /// pasa este conjunto tal cual a <c>AtribucionCalculador.Calcular</c> (parametro
    /// <c>recursosConReservaConfirmada</c>): son los unicos recursos que "gana la reserva" (E3) puede
    /// sacarle a los baldes 2 y 3 sin romper la invariante de E9. Vacia (nunca con recursos cuya
    /// reserva arranco antes o despues de la ventana del informe), no solo cuando no hay reservas.
    /// </summary>
    [property: JsonPropertyName("recursosQueExplicanElPeriodo")] IReadOnlyList<string> RecursosQueExplicanElPeriodo,
    /// <summary>
    /// Cuantas de las reservas activas de la foto no pudieron informar sus consumidores
    /// (<see cref="Recolector.ReservaActiva.ConsumidoresNoLeidos"/>). Mayor a cero significa que
    /// <see cref="AhorroConfirmado"/>, <see cref="Confirmados"/>, <see cref="AporteAlPeriodo"/> y
    /// <see cref="RecursosQueExplicanElPeriodo"/> estan INCOMPLETOS: la parte que no se pudo leer
    /// figura entera en <see cref="Estimados"/>, y los recursos que esas reservas cubren siguen
    /// contados en los baldes 2 y 3 porque no hay forma de saber cuales son.
    ///
    /// <para>Sin este campo, el caso se publicaba identico al legitimo (una reserva que todavia no
    /// tiene consumidores confirmados): la marca existia en la foto pero moria ahi, y quien leyera la
    /// respuesta no tenia como distinguir "no hay ahorro confirmado" de "no se pudo medir". El eje NO
    /// se declara "no medido" por esto —la lista de reservas si se leyo, y el resto de la foto es
    /// valido— pero la cifra viaja rotulada, mismo criterio que los ejes de <c>EstadoRbac</c>.
    /// <see cref="Motivo"/> lo dice tambien en texto (ver <c>ReservasRecolector</c>).</para>
    /// </summary>
    [property: JsonPropertyName("reservasConConsumidoresNoLeidos")] int ReservasConConsumidoresNoLeidos);

/// <summary>
/// El ahorro de un recurso confirmado como consumidor de una reserva especifica. La ventana
/// antes/despues la marca <see cref="InicioReserva"/> (derivado de ExpiresOn menos Term en la
/// foto), nunca el mes en que bajo la factura.
/// </summary>
public sealed record AhorroPorRecurso(
    [property: JsonPropertyName("resourceName")] string? ResourceName,
    [property: JsonPropertyName("resourceGroup")] string? ResourceGroup,
    [property: JsonPropertyName("subscriptionId")] string? SubscriptionId,
    [property: JsonPropertyName("reservationId")] string? ReservationId,
    [property: JsonPropertyName("reservationName")] string? ReservationName,
    [property: JsonPropertyName("term")] string? Term,
    /// <summary>"aaaa-MM-dd", derivado de ExpiresOn - Term. Null cuando no se pudo derivar (ver
    /// <see cref="MotivoSinCalcular"/>).</summary>
    [property: JsonPropertyName("inicioReserva")] string? InicioReserva,
    /// <summary>Horas del beneficio efectivamente consumidas, tal cual las reporta Azure
    /// (<c>UsedHours</c> de <see cref="Recolector.ConsumidorReserva"/>): nunca se reconstruyen
    /// desde la factura.</summary>
    [property: JsonPropertyName("usedHours")] double UsedHours,
    /// <summary>Utilizacion de la reserva completa (no solo de este recurso), publicada junto al
    /// ahorro: un ahorro alto con utilizacion baja significa que se compro de mas.</summary>
    [property: JsonPropertyName("utilizationLast")] string? UtilizationLast,
    [property: JsonPropertyName("utilization7d")] string? Utilization7d,
    /// <summary>La reserva que cubre este recurso esta cerca de vencer (mismo umbral de la foto):
    /// el ahorro atribuido tiene fecha de vencimiento.</summary>
    [property: JsonPropertyName("expiring")] bool Expiring,
    [property: JsonPropertyName("tarifaAntesPorHora")] decimal? TarifaAntesPorHora,
    [property: JsonPropertyName("tarifaDespuesPorHora")] decimal? TarifaDespuesPorHora,
    /// <summary>(TarifaAntesPorHora - TarifaDespuesPorHora) * UsedHours. Null cuando falta
    /// facturacion de un lado de la ventana, o cuando no se pudo derivar
    /// <see cref="InicioReserva"/> — ver <see cref="MotivoSinCalcular"/>. No se fuerza a cero
    /// cuando sale negativo o cero: eso es la señal de una discrepancia (ver
    /// <see cref="AhorroReservasModelo.Discrepancias"/>), no un error de calculo a esconder.</summary>
    [property: JsonPropertyName("ahorro")] decimal? Ahorro,
    /// <summary>Por que <see cref="Ahorro"/> es null, cuando lo es. Null cuando si se calculo.</summary>
    [property: JsonPropertyName("motivoSinCalcular")] string? MotivoSinCalcular,
    /// <summary>
    /// E9: <c>true</c> cuando <see cref="InicioReserva"/> cae DENTRO de la ventana fija del informe
    /// (estrictamente despues de su primer mes, y no despues de su ultimo) — la unica condicion bajo
    /// la que esta reserva explica algo de lo que paso EN EL PERIODO, no solo desde su propio inicio.
    /// Una reserva que arranco antes de que empiece la ventana ya tenia a este recurso cubierto
    /// durante TODO el periodo: cualquier variacion que se vea adentro no la causo la reserva (la
    /// cobertura no cambio ahi), asi que <c>false</c> — no <c>true</c> con un aporte que salga cero
    /// por casualidad. Null de <see cref="InicioReserva"/> (termino no reconocido, sin fecha de
    /// vencimiento) tambien da <c>false</c>: sin fecha no hay como demostrar que explica algo.
    /// </summary>
    [property: JsonPropertyName("explicaElPeriodo")] bool ExplicaElPeriodo,
    /// <summary>
    /// El aporte de este recurso a la variacion del PERIODO del informe: promedio de la ventana base
    /// menos promedio de la ventana de cierre (mismas dos ventanas fijas que
    /// <see cref="AtribucionCalculador"/>, sobre la facturacion cruda del recurso — nunca la tarifa
    /// por hora de <see cref="Ahorro"/>), redondeado para MOSTRARSE (el total de nivel superior,
    /// <see cref="AhorroReservasModelo.AporteAlPeriodo"/>, se calcula sobre la suma SIN redondear,
    /// mismo criterio E1 que <see cref="AtribucionRecurso.Delta"/>). Null cuando
    /// <see cref="ExplicaElPeriodo"/> es <c>false</c> (no aplica, no es que sea cero) o cuando la
    /// ventana fija del informe no se pudo calcular (menos de seis meses no parciales en rango).
    /// </summary>
    [property: JsonPropertyName("aporteAlPeriodo")] decimal? AporteAlPeriodo);

/// <summary>Unidades reservadas sin un consumidor confirmado (E2): la diferencia entre lo
/// comprado y lo que la app pudo atar a un recurso puntual. Sin terna propia, asi que se publica
/// rotulado como estimado, sin costo ni discrepancia asociada.</summary>
public sealed record EstimadoPorReserva(
    [property: JsonPropertyName("reservationId")] string? ReservationId,
    [property: JsonPropertyName("nombre")] string? Nombre,
    [property: JsonPropertyName("producto")] string? Producto,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("term")] string? Term,
    [property: JsonPropertyName("unidadesEstimadas")] int UnidadesEstimadas,
    /// <summary><c>true</c> cuando estas unidades figuran como estimadas porque la lectura de
    /// consumidores de esta reserva puntual fallo, no porque no tenga consumidores confirmados (ver
    /// <see cref="AhorroReservasModelo.ReservasConConsumidoresNoLeidos"/>). Es la unica marca por
    /// reserva: sin ella, una reserva enteramente estimada por falla de permisos se lee igual que
    /// una que de verdad nadie esta consumiendo, y son conclusiones opuestas.</summary>
    [property: JsonPropertyName("consumidoresNoLeidos")] bool ConsumidoresNoLeidos);

/// <summary>Un recurso con cobertura confirmada por la app cuya facturacion no muestra el efecto
/// esperado (E2: "la facturacion sirve para contrastar, no para decidir"). El informe no elige
/// cual de las dos fuentes esta desactualizada.</summary>
public sealed record DiscrepanciaCobertura(
    [property: JsonPropertyName("resourceName")] string? ResourceName,
    [property: JsonPropertyName("resourceGroup")] string? ResourceGroup,
    [property: JsonPropertyName("subscriptionId")] string? SubscriptionId,
    [property: JsonPropertyName("reservationId")] string? ReservationId,
    [property: JsonPropertyName("detalle")] string Detalle);
