using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;

/// <summary>Una acción de optimización ejecutada: la unidad de la PPT de referencia. El monto
/// lleva su fuente rotulada (decisión 2026-08-13): "facturado" = el delta real del recurso en
/// BITCOST; "estimado" = estimated_monthly_savings del barrido; null = sin monto, con motivo,
/// fuera de la aritmética pero visible en la tabla.
/// <para><b>MesEjecucion del barrido = updated_at, que NO garantiza la fecha real de
/// resolución</b>: el reconcile la avanza si el hallazgo reaparece (herencia de la entrega 5,
/// OptimizationService.ReconcileAsync). Por eso el registro publica la fecha como "última
/// actualización del estado" en la nota cuando la autoría es indeterminada.</para></summary>
public sealed record AccionEjecutada(
    [property: JsonPropertyName("fuente")] string Fuente,           // "barrido" | "matriz" | "reserva"
    [property: JsonPropertyName("oportunidad")] string Oportunidad, // check / hallazgo / nombre de reserva
    [property: JsonPropertyName("cat")] string Categoria,
    [property: JsonPropertyName("sub")] string? SubscriptionId,
    [property: JsonPropertyName("rg")] string? ResourceGroup,
    [property: JsonPropertyName("rec")] string? ResourceName,
    [property: JsonPropertyName("mes")] string MesEjecucion,        // "aaaa-MM"
    [property: JsonPropertyName("fin")] string? MesFin,             // "aaaa-MM": solo reservas (vencimiento)
    [property: JsonPropertyName("monto")] decimal? MontoMensual,    // YA redondeado (E1)
    [property: JsonPropertyName("fuenteMonto")] string? FuenteMonto,// "facturado" | "estimado" | null
    [property: JsonPropertyName("sinMonto")] string? MotivoSinMonto,
    [property: JsonPropertyName("autoria")] string Autoria);        // "declarada" | "automatica" | "indeterminada"

/// <summary>Qué ejes del registro se pudieron medir (D9): el informe declara, no rellena.</summary>
public sealed record RegistroEjes(
    [property: JsonPropertyName("barridoMedido")] bool BarridoMedido,
    [property: JsonPropertyName("barridoMotivo")] string? BarridoMotivo,
    [property: JsonPropertyName("reservasMedidas")] bool ReservasMedidas,
    [property: JsonPropertyName("reservasMotivo")] string? ReservasMotivo,
    [property: JsonPropertyName("indeterminadas")] int Indeterminadas); // filas con autoría indeterminada
