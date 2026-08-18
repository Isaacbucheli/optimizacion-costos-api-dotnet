using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// La línea de tiempo del servicio, derivada de la bitácora de la matriz de mejoras (decisión del
/// 2026-08-13: derivada, sin tabla propia). Solo hitos de BIT: cuando el relato necesite la
/// respuesta del cliente, la sección lo declara.
///
/// <para><b>Lista blanca de campos, no filtro del recolector.</b> <c>CronologiaRecolector</c> trae
/// los siete campos trackeados a propósito (es su punto de extensión), y dos de ellos
/// —<c>internal_notes</c> y <c>execution_log</c>— son texto interno libre que jamás puede viajar en
/// la variante del cliente. <see cref="CamposPublicables"/> es la puerta: lo que no está, no se
/// dibuja, ni siquiera en la variante interna, para que las dos variantes cuenten lo mismo.</para>
/// </summary>
public sealed record CronologiaModelo(
    [property: JsonPropertyName("hitos")] IReadOnlyList<HitoModelo> Hitos,
    /// <summary>Cuántas entradas de la bitácora quedaron fuera, por dos causas distintas que el
    /// mapeador (<c>InformeValorEnsamblador.MapearCronologia</c>) cuenta juntas: no estar en la lista
    /// blanca de <see cref="CamposPublicables"/> (notas internas, bitácora de ejecución, esfuerzo,
    /// prioridad), o caer fuera de la ventana del informe. Se publica para que nadie lea una
    /// cronología corta -o vacía- como "no pasó nada" (ver el fix del artefacto: hitos.length===0 con
    /// omitidos&gt;0 nunca dibuja el mensaje de "sin hitos" sin más).</summary>
    [property: JsonPropertyName("omitidos")] int Omitidos)
{
    /// <summary>Los campos de <c>waf_tracking_history</c> que cuentan una historia publicable: el
    /// avance comprometido y las fechas de remediación. Fuera quedan las notas internas, la
    /// bitácora de ejecución, el esfuerzo estimado y la prioridad, que son gestión de BIT.</summary>
    public static readonly IReadOnlyList<string> CamposPublicables =
        ["completion_pct", "remediation_start_date", "remediation_end_date"];
}

/// <summary>Un hito de la línea de tiempo.</summary>
public sealed record HitoModelo(
    [property: JsonPropertyName("fecha")] string Fecha,           // "aaaa-MM-dd"
    [property: JsonPropertyName("campo")] string Campo,
    [property: JsonPropertyName("antes")] string? Antes,
    [property: JsonPropertyName("despues")] string? Despues,
    [property: JsonPropertyName("rec")] string Recomendacion,
    [property: JsonPropertyName("codigo")] string? MatrixCode,
    [property: JsonPropertyName("pilar")] int Pilar);
