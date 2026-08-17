using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;

/// <summary>El titular del informe (decisión 2026-08-13): el acumulado de lo ejecutado, el
/// modelo de la PPT de referencia. Aritmética verificada contra la PPT al centavo:
/// tasaVigente(m) = suma de filas con monto ejecutadas hasta m y aún vigentes (una reserva
/// deja de sumar después de su MesFin); acumulado(m) = acumulado(m-1) + tasaVigente(m).
/// La ventana es la del informe: las series cubren los meses del rango; una acción anterior
/// al rango vigente sigue sumando su tasa (su ahorro se sigue percibiendo).
/// <para>Esta acumulación NO es la variación del consumo de la 2d ni tiene que igualarla:
/// mide desde el mes de ejecución de cada acción (el encuadre de la PPT), mientras la
/// variación mide sobre la ventana fija (E9). Las dos cifras conviven declaradas
/// (docs/informe-valor-divergencias.md).</para></summary>
public sealed record EjecutadoModelo(
    [property: JsonPropertyName("medido")] bool Medido,
    [property: JsonPropertyName("motivo")] string? Motivo,
    [property: JsonPropertyName("filas")] IReadOnlyList<AccionEjecutada> Filas,
    // [mes "aaaa-MM", tasa vigente, acumulado] — posicional, mismo estilo de fact.meses
    [property: JsonPropertyName("serie")] IReadOnlyList<IReadOnlyList<object?>> Serie,
    // [oportunidad, acumulado del rango] ordenado descendente — el gráfico por oportunidad de la PPT
    [property: JsonPropertyName("porOportunidad")] IReadOnlyList<IReadOnlyList<object?>> PorOportunidad,
    // categoría → (mes → tasa vigente de esa categoría) — el apilado; mismo shape que catSerie
    [property: JsonPropertyName("catAcum")] IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> PorCategoria,
    [property: JsonPropertyName("total")] decimal AcumuladoTotal,          // acumulado del último mes del rango
    [property: JsonPropertyName("tasaVigente")] decimal TasaVigenteCierre, // tasa del último mes del rango
    [property: JsonPropertyName("pctGasto")] decimal? PctGastoPeriodo,     // tarjeta 1: total/gasto, 1 decimal; null si gasto no medible
    [property: JsonPropertyName("facturado")] decimal MontoFacturado,      // composición del total, declarada
    [property: JsonPropertyName("estimado")] decimal MontoEstimado,
    [property: JsonPropertyName("sinMonto")] int FilasSinMonto,
    // [mes, tasa proyectada, acumulado proyectado] desde el mes siguiente al corte hasta diciembre del año del corte
    [property: JsonPropertyName("proyeccion")] IReadOnlyList<IReadOnlyList<object?>> Proyeccion,
    [property: JsonPropertyName("proyeccionFin")] decimal? ProyeccionFinDeAnio,
    [property: JsonPropertyName("reservas")] ReservasFacturadasModelo Reservas,
    [property: JsonPropertyName("ejes")] RegistroEjes Ejes);
