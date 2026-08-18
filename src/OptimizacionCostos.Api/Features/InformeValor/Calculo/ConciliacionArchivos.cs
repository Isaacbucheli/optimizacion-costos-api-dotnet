using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// Tarea 8 de la entrega 6 (spec, "Reglas de convivencia entre los dos archivos"): los totales por
/// mes de la tabla de hechos contra los del archivo de evolución, dentro del rango del informe. No
/// se promedia ni se elige una fuente en silencio: si difieren más allá del umbral, el informe lo
/// declara con la cifra de cada fuente. La tabla de hechos manda para identidad y agrupaciones; la
/// evolución, para reservas — esta conciliación no decide cuál de las dos "vale", solo dice cuándo
/// dejaron de estar de acuerdo.
///
/// <para><see cref="Umbral"/> es la tasa del 0.5%, no un monto: el umbral real que se aplica mes a
/// mes es <c>max(1.00, 0.5% del total de hechos de ESE mes)</c> — <see cref="InformeValorEnsamblador"/>
/// calcula ese máximo por mes y lo usa para decidir qué filas entran a <see cref="Diferencias"/>,
/// nunca contra un monto fijo en dólares (un umbral fijo sobrerreacciona en un cliente chico y se
/// queda corto en uno grande). El piso de $1 no viaja acá porque es una constante de la regla, no un
/// dato del cliente; lo que sí viaja en cada fila ya es el resultado de aplicar los dos, así que no
/// hace falta reconstruir el cálculo para leer la lista.</para>
///
/// <para><see cref="Coincide"/> es <c>true</c> solo cuando <see cref="Diferencias"/> queda vacía:
/// ningún mes superó su propio umbral.</para>
/// </summary>
public sealed record ConciliacionArchivos(
    [property: JsonPropertyName("coincide")] bool Coincide,
    // [mes "aaaa-MM", total hechos, total evolución, diferencia] — solo meses con diferencia sobre el umbral
    [property: JsonPropertyName("difs")] IReadOnlyList<IReadOnlyList<object?>> Diferencias,
    [property: JsonPropertyName("umbralTasa")] decimal Umbral);
