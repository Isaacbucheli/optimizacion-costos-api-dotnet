namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Helpers puros del histórico de Advisor Score: agregación por cliente (ponderada por consumo),
/// forma de la respuesta de la API y conversión de granularidad. Sin dependencias de BD/HTTP.
/// </summary>
public static class ScoreHistory
{
    private static readonly int[] SeriesKeys = [0, 1, 2, 3, 4, 5]; // 0=global, 1..5=pilar

    /// <summary>
    /// Agrega el histórico de varias suscripciones a nivel cliente. Por granularidad, por serie y por
    /// fecha: media ponderada por Weight; si el peso total es 0, media aritmética. Redondeo a 2.
    /// </summary>
    public static IReadOnlyList<ClientScoreHistory> Aggregate(IEnumerable<SubscriptionScoreHistory> histories)
    {
        // gran -> serie -> fecha -> (weightedSum, weightSum, simpleSum, count)
        var acc = new Dictionary<char, Dictionary<int, Dictionary<DateOnly, Accum>>>();

        foreach (var sub in histories)
        {
            var byGran = acc.TryGetValue(sub.Granularity, out var g) ? g : (acc[sub.Granularity] = new());
            foreach (var (series, points) in sub.Series)
            {
                var byDate = byGran.TryGetValue(series, out var d) ? d : (byGran[series] = new());
                foreach (var pt in points)
                {
                    var a = byDate.TryGetValue(pt.Date, out var cur) ? cur : new Accum();
                    var score = (double)pt.Score;
                    var weight = (double)pt.Weight;
                    a.WeightedSum += score * weight;
                    a.WeightSum += weight;
                    a.SimpleSum += score;
                    a.Count += 1;
                    byDate[pt.Date] = a;
                }
            }
        }

        var result = new List<ClientScoreHistory>();
        foreach (var (gran, byseries) in acc)
        {
            // Reunir todas las fechas presentes en cualquier serie de esta granularidad.
            var dates = byseriesDates(byseries);
            var points = new List<ClientScoreHistoryPoint>(dates.Count);
            foreach (var date in dates)
            {
                var series = new Dictionary<int, decimal>();
                foreach (var key in SeriesKeys)
                {
                    if (!byseries.TryGetValue(key, out var byDate) || !byDate.TryGetValue(date, out var a)) continue;
                    var v = a.WeightSum > 0 ? a.WeightedSum / a.WeightSum : a.SimpleSum / a.Count;
                    series[key] = (decimal)Math.Round(v, 2);
                }
                if (series.Count > 0) points.Add(new ClientScoreHistoryPoint(date, series));
            }
            result.Add(new ClientScoreHistory(gran, points));
        }
        return result;
    }

    private static List<DateOnly> byseriesDates(Dictionary<int, Dictionary<DateOnly, Accum>> byseries)
    {
        var set = new SortedSet<DateOnly>();
        foreach (var byDate in byseries.Values)
            foreach (var date in byDate.Keys)
                set.Add(date);
        return set.ToList();
    }

    private struct Accum
    {
        public double WeightedSum;
        public double WeightSum;
        public double SimpleSum;
        public int Count;
    }

    public static char GranularityChar(string? granularity) =>
        (granularity ?? "").Trim().ToLowerInvariant() switch { "day" => 'D', "week" => 'W', _ => 'M' };

    public static string GranularityName(char granularity) =>
        granularity switch { 'D' => "day", 'W' => "week", _ => "month" };

    /// <summary>Forma snake_case de la respuesta del endpoint de historial (global + pilares 1..5).</summary>
    public static object BuildResponse(string granularity, IReadOnlyList<ClientScoreHistoryPoint> points) => new
    {
        granularity,
        series = points.Select(p => new
        {
            date = p.Date.ToString("yyyy-MM-dd"),
            global = p.Series.TryGetValue(0, out var g) ? (decimal?)g : null,
            pillars = new Dictionary<string, decimal?>
            {
                ["1"] = p.Series.TryGetValue(1, out var v1) ? v1 : null,
                ["2"] = p.Series.TryGetValue(2, out var v2) ? v2 : null,
                ["3"] = p.Series.TryGetValue(3, out var v3) ? v3 : null,
                ["4"] = p.Series.TryGetValue(4, out var v4) ? v4 : null,
                ["5"] = p.Series.TryGetValue(5, out var v5) ? v5 : null,
            },
        }),
    };
}
