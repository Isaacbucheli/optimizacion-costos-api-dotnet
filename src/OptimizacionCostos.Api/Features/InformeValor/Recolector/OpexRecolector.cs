using Microsoft.Data.SqlClient;
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
        var hist = await store.LoadHistoryAsync(clientId, 'M', ct);
        var snap = await store.LoadLatestSnapshotAsync(clientId, includeBreakdown: false, ct);
        return Armar(snap, hist);
    }

    /// <summary>
    /// Mismo resultado que la sobrecarga de arriba, pero sobre la conexión compartida de
    /// <c>SqlInsumosBdRecolector</c> cuando el store inyectado es el SQL real (entrega 6 tarea
    /// 11, hallazgo del review final de la entrega 5): sin esta sobrecarga, cada preview abría 2
    /// conexiones nuevas y corría 2 veces el schema-ensure de WAF solo para leer Opex, en un App
    /// Service B1 compartido. Llama las sobrecargas internas de <c>SqlAdvisorScoreStore</c> que
    /// reciben la conexión ya abierta -- el schema WAF ya está asegurado por el llamador.
    /// </summary>
    public static async Task<OpexScore> LeerAsync(
        SqlConnection conn, SqlAdvisorScoreStore store, int clientId, CancellationToken ct = default)
    {
        var hist = await SqlAdvisorScoreStore.LoadHistoryAsync(conn, clientId, 'M', ct);
        var snap = await SqlAdvisorScoreStore.LoadLatestSnapshotAsync(conn, clientId, includeBreakdown: false, ct);
        return Armar(snap, hist);
    }

    /// <summary>
    /// El mapeo de snapshot + historia a <see cref="OpexScore"/>, compartido por las dos
    /// sobrecargas de <c>LeerAsync</c> de arriba: sin este método común, la clave 5 del
    /// diccionario y los dos motivos de "no medido" iban a vivir duplicados en dos lugares que
    /// tarde o temprano se iban a desalinear. Internal (no private) para que
    /// <c>OpexRecolectorTests.ArmarTests</c> lo pruebe directo, sin necesidad de una conexión SQL
    /// real: es la única forma práctica de confirmar que las dos sobrecargas dan lo mismo.
    /// </summary>
    internal static OpexScore Armar(WafAdvisorScoreSnapshot? snap, IReadOnlyList<ClientScoreHistoryPoint> hist)
    {
        var puntos = new List<OpexPunto>();
        foreach (var p in hist)
            if (p.Series.TryGetValue(5, out var v))
                puntos.Add(new OpexPunto(p.Date, v));

        if (snap is null)
            return new OpexScore(null, null, null, puntos, false,
                "El cliente no tiene ningún snapshot de Advisor Score.");
        if (snap.ScoreP5 is null)
            return new OpexScore(null, snap.SnapshotDate, snap.Status, puntos, false,
                "El snapshot más reciente no trae el pilar de costos.");
        return new OpexScore(snap.ScoreP5, snap.SnapshotDate, snap.Status, puntos, true, null);
    }
}
