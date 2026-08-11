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
/// </summary>
public sealed record AhorroReservasModelo(
    bool Medido,
    string Motivo,
    IReadOnlyList<object> Errores,
    int AlertDays,
    /// <summary>Suma de <see cref="AhorroPorRecurso.Ahorro"/> entre los <see cref="Confirmados"/>
    /// que si se pudieron calcular. Cero cuando <see cref="Medido"/> es <c>false</c> — no es que no
    /// haya ahorro, es que no se midio (ver <see cref="Medido"/>).</summary>
    decimal AhorroConfirmado,
    IReadOnlyList<AhorroPorRecurso> Confirmados,
    IReadOnlyList<EstimadoPorReserva> Estimados,
    IReadOnlyList<DiscrepanciaCobertura> Discrepancias);

/// <summary>
/// El ahorro de un recurso confirmado como consumidor de una reserva especifica. La ventana
/// antes/despues la marca <see cref="InicioReserva"/> (derivado de ExpiresOn menos Term en la
/// foto), nunca el mes en que bajo la factura.
/// </summary>
public sealed record AhorroPorRecurso(
    string? ResourceName,
    string? ResourceGroup,
    string? SubscriptionId,
    string? ReservationId,
    string? ReservationName,
    string? Term,
    /// <summary>"aaaa-MM-dd", derivado de ExpiresOn - Term. Null cuando no se pudo derivar (ver
    /// <see cref="MotivoSinCalcular"/>).</summary>
    string? InicioReserva,
    /// <summary>Horas del beneficio efectivamente consumidas, tal cual las reporta Azure
    /// (<c>UsedHours</c> de <see cref="Recolector.ConsumidorReserva"/>): nunca se reconstruyen
    /// desde la factura.</summary>
    double UsedHours,
    /// <summary>Utilizacion de la reserva completa (no solo de este recurso), publicada junto al
    /// ahorro: un ahorro alto con utilizacion baja significa que se compro de mas.</summary>
    string? UtilizationLast,
    string? Utilization7d,
    /// <summary>La reserva que cubre este recurso esta cerca de vencer (mismo umbral de la foto):
    /// el ahorro atribuido tiene fecha de vencimiento.</summary>
    bool Expiring,
    decimal? TarifaAntesPorHora,
    decimal? TarifaDespuesPorHora,
    /// <summary>(TarifaAntesPorHora - TarifaDespuesPorHora) * UsedHours. Null cuando falta
    /// facturacion de un lado de la ventana, o cuando no se pudo derivar
    /// <see cref="InicioReserva"/> — ver <see cref="MotivoSinCalcular"/>. No se fuerza a cero
    /// cuando sale negativo o cero: eso es la señal de una discrepancia (ver
    /// <see cref="AhorroReservasModelo.Discrepancias"/>), no un error de calculo a esconder.</summary>
    decimal? Ahorro,
    /// <summary>Por que <see cref="Ahorro"/> es null, cuando lo es. Null cuando si se calculo.</summary>
    string? MotivoSinCalcular);

/// <summary>Unidades reservadas sin un consumidor confirmado (E2): la diferencia entre lo
/// comprado y lo que la app pudo atar a un recurso puntual. Sin terna propia, asi que se publica
/// rotulado como estimado, sin costo ni discrepancia asociada.</summary>
public sealed record EstimadoPorReserva(
    string? ReservationId,
    string? Nombre,
    string? Producto,
    string? Region,
    string? Term,
    int UnidadesEstimadas);

/// <summary>Un recurso con cobertura confirmada por la app cuya facturacion no muestra el efecto
/// esperado (E2: "la facturacion sirve para contrastar, no para decidir"). El informe no elige
/// cual de las dos fuentes esta desactualizada.</summary>
public sealed record DiscrepanciaCobertura(
    string? ResourceName,
    string? ResourceGroup,
    string? SubscriptionId,
    string? ReservationId,
    string Detalle);
