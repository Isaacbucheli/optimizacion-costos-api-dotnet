using System.Text.Json.Serialization;

namespace OptimizacionCostos.Api.Features.InformeValor.Calculo;

/// <summary>
/// El score del pilar de costos de Azure Advisor, con su evolución mensual. Alimenta la tarjeta
/// "Opex" del resumen (observación 3 de la reunión del 2026-08-13) y el gráfico grande de la
/// sección Advisor.
///
/// <para><b>Clave de nivel superior y no dentro de <c>advisor</c></b>: <see cref="PosturaModelo"/> es null
/// cuando el cliente no tiene recomendaciones activas, y un cliente puede tener score sin
/// recomendaciones. Anidarlo perdería el dato justo en el caso que la tarjeta existe para contar.</para>
///
/// <para><see cref="Medido"/>=false con <see cref="Motivo"/> cuando no hay snapshot o el snapshot no
/// trae el pilar: la tarjeta dice "sin medición", nunca 0% (D9).</para>
/// </summary>
public sealed record OpexModelo(
    [property: JsonPropertyName("actual")] decimal? Actual,
    [property: JsonPropertyName("fecha")] string? Fecha,          // "aaaa-MM-dd" del snapshot
    [property: JsonPropertyName("estado")] string? Estado,
    /// <summary>[mes "aaaa-MM", score] por punto mensual, orden cronológico. Posicional, igual que
    /// el resto de las series del modelo.</summary>
    [property: JsonPropertyName("serie")] IReadOnlyList<IReadOnlyList<object?>> Serie,
    [property: JsonPropertyName("medido")] bool Medido,
    [property: JsonPropertyName("motivo")] string? Motivo);
