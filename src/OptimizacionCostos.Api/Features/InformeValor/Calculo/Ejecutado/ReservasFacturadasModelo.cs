using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;

/// <summary>La tabla "reservas contra la propia factura": por cada VM cubierta, lo que costaba
/// por demanda el último mes completo antes del inicio de la reserva (BITCOST, tabla de hechos)
/// contra el cargo mensual facturado de la reserva (archivo de evolución, líneas
/// "Reserved VM Instance, SKU, región, término"). El precio de la reserva NO existe en la tabla
/// de hechos: por eso el segundo archivo es obligatorio (spec, Insumos).
/// Reservas compartidas por SKU se prorratean entre sus VM y quedan marcadas.
/// D9: <see cref="Medido"/>=false con motivo cuando la foto no midió o la evolución no trae
/// líneas de reserva — nunca una tabla vacía que simule "sin reservas".</summary>
public sealed record ReservasFacturadasModelo(
    [property: JsonPropertyName("medido")] bool Medido,
    [property: JsonPropertyName("motivo")] string? Motivo,
    [property: JsonPropertyName("filas")] IReadOnlyList<ReservaVmFila> Filas,
    [property: JsonPropertyName("totalDemanda")] decimal TotalDemanda,
    [property: JsonPropertyName("totalReserva")] decimal TotalReserva,
    [property: JsonPropertyName("totalAhorro")] decimal TotalAhorro,
    [property: JsonPropertyName("ahorroAnualizado")] decimal AhorroAnualizado,      // TotalAhorro*12, redondeado una vez
    [property: JsonPropertyName("sinLineaEnEvolucion")] IReadOnlyList<string> SinLineaEnEvolucion, // reservas de la foto sin match (p. ej. no-VM: is_reservation solo detecta VM)
    [property: JsonPropertyName("consumidoresNoLeidos")] int ConsumidoresNoLeidos);

public sealed record ReservaVmFila(
    [property: JsonPropertyName("reservationId")] string? ReservationId, // la ReservaActiva que originó esta fila (Tarea 4: sumar AhorroMes por reserva)
    [property: JsonPropertyName("vm")] string Vm,
    [property: JsonPropertyName("sku")] string? Sku,
    [property: JsonPropertyName("demanda")] decimal? PorDemandaMes,   // null = sin mes base en BITCOST
    [property: JsonPropertyName("reserva")] decimal ReservaMes,       // prorrateado si compartida
    [property: JsonPropertyName("ahorro")] decimal? AhorroMes,        // demanda - reserva; null si demanda null
    [property: JsonPropertyName("compartida")] bool Compartida,       // prorrateo entre varias VM
    [property: JsonPropertyName("vence")] string? Vence,              // ExpiresOn "aaaa-MM-dd"
    [property: JsonPropertyName("porVencer")] bool PorVencer,         // Expiring de la foto
    [property: JsonPropertyName("nota")] string? Nota);
