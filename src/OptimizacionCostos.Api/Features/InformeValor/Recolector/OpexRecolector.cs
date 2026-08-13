using OptimizacionCostos.Api.Features.Waf;

namespace OptimizacionCostos.Api.Features.InformeValor.Recolector;

/// <summary>Score del pilar de costos hoy. La ventana del informe NO se aplica acá: la serie
/// viaja completa y la calculadora (entrega 6) recorta al rango, igual que todos los
/// recolectores (D0). <see cref="Medido"/>=false con <see cref="Motivo"/> cuando no hay
/// snapshot o el snapshot no trae el pilar: la tarjeta dice "sin medición", nunca 0%.</summary>
public sealed record OpexScore(
    decimal? Actual,
    DateOnly? SnapshotDate,
    string? Status,
    IReadOnlyList<OpexPunto> Serie,
    bool Medido,
    string? Motivo);

public sealed record OpexPunto(DateOnly Fecha, decimal Score);

/// <summary>Lee el score del pilar de costos (5) de Advisor: el snapshot más reciente para la
/// cifra actual y la serie mensual de <c>waf_advisor_score_history</c> para la evolución.
/// Es la fuente de la tarjeta "Opex" del resumen (observación 3 de la reunión 2026-08-13) y
/// del gráfico grande de la sección Advisor.</summary>
public static class OpexRecolector
{
    public static async Task<OpexScore> LeerAsync(IAdvisorScoreStore store, int clientId, CancellationToken ct = default)
    {
        var puntos = new List<OpexPunto>();
        foreach (var p in await store.LoadHistoryAsync(clientId, 'M', ct))
            if (p.Series.TryGetValue(5, out var v))
                puntos.Add(new OpexPunto(p.Date, v));

        var snap = await store.LoadLatestSnapshotAsync(clientId, includeBreakdown: false, ct);
        if (snap is null)
            return new OpexScore(null, null, null, puntos, false,
                "El cliente no tiene ningún snapshot de Advisor Score.");
        if (snap.ScoreP5 is null)
            return new OpexScore(null, snap.SnapshotDate, snap.Status, puntos, false,
                "El snapshot más reciente no trae el pilar de costos.");
        return new OpexScore(snap.ScoreP5, snap.SnapshotDate, snap.Status, puntos, true, null);
    }
}
