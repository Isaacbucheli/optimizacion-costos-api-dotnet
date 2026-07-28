using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Filtro por suscripción de la matriz WAF (estilo portal de Advisor). La suscripción vive en
/// waf_resource_finding (nivel recurso), no en waf_recommendation: una recomendación puede tocar
/// varias, así que filtrar es "tiene al menos un hallazgo activo en la selección".
///
/// Sin selección NO se cambia nada: los fragmentos quedan vacíos y el conteo sigue leyendo la
/// columna denormalizada resource_count (ruta rápida, comportamiento idéntico al de siempre).
/// </summary>
public static class WafSubscriptionFilter
{
    /// <summary>Parsea el parámetro `subscriptions` (ids separados por coma). Dedupe case-insensitive.</summary>
    public static IReadOnlyList<string> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (seen.Add(part)) result.Add(part);
        return result;
    }

    public static bool IsActive(IReadOnlyList<string> subscriptions) => subscriptions.Count > 0;

    /// <summary>
    /// Predicado para quedarse solo con las recomendaciones que tienen hallazgo activo en la
    /// selección. Cadena vacía cuando no hay filtro (se concatena tal cual al WHERE).
    /// </summary>
    public static string ExistsPredicate(string recAlias, IReadOnlyList<string> subscriptions) =>
        !IsActive(subscriptions)
            ? ""
            // El salto inicial no es cosmético: el fragmento se concatena a un WHERE ya escrito.
            : $"""
               {"\n"}  AND EXISTS (
                      SELECT 1 FROM dbo.waf_resource_finding wsf
                      WHERE wsf.recommendation_id = {recAlias}.recommendation_id
                        AND wsf.status = 'active'
                        AND wsf.subscription_id IN ({ParamNames(subscriptions)}))
               """;

    /// <summary>
    /// Expresión del conteo de recursos. Con filtro NO se puede usar la columna denormalizada
    /// resource_count (es el total del cliente): se cuenta sobre los hallazgos de la selección.
    /// </summary>
    public static string ResourceCountExpr(string recAlias, IReadOnlyList<string> subscriptions) =>
        !IsActive(subscriptions)
            ? $"{recAlias}.resource_count"
            : $"""
               (SELECT COUNT(*) FROM dbo.waf_resource_finding wsc
                WHERE wsc.recommendation_id = {recAlias}.recommendation_id
                  AND wsc.status = 'active'
                  AND wsc.subscription_id IN ({ParamNames(subscriptions)}))
               """;

    /// <summary>
    /// Variante para consultas que AGREGAN el conteo (SUM por pilar): T-SQL no admite un agregado
    /// sobre una subconsulta, así que el conteo entra por CROSS APPLY. Sin filtro no agrega nada y
    /// el SUM sigue siendo sobre la columna denormalizada. Se usa junto con ResourceCountAggregate.
    /// </summary>
    public static string ResourceCountApply(string recAlias, IReadOnlyList<string> subscriptions) =>
        !IsActive(subscriptions)
            ? ""
            : $"""
               {"\n"}CROSS APPLY (
                   SELECT COUNT(*) AS resource_count
                   FROM dbo.waf_resource_finding wsa
                   WHERE wsa.recommendation_id = {recAlias}.recommendation_id
                     AND wsa.status = 'active'
                     AND wsa.subscription_id IN ({ParamNames(subscriptions)})) rc
               """;

    /// <summary>Columna a agregar, en pareja con ResourceCountApply.</summary>
    public static string ResourceCountAggregate(string recAlias, IReadOnlyList<string> subscriptions) =>
        IsActive(subscriptions) ? "rc.resource_count" : $"{recAlias}.resource_count";

    /// <summary>Predicado sobre un alias de hallazgo ya presente en la consulta (ej. el LEFT JOIN del summary).</summary>
    public static string FindingPredicate(string findingAlias, IReadOnlyList<string> subscriptions) =>
        !IsActive(subscriptions) ? "" : $" AND {findingAlias}.subscription_id IN ({ParamNames(subscriptions)})";

    public static string ParamNames(IReadOnlyList<string> subscriptions) =>
        string.Join(",", subscriptions.Select((_, i) => $"@sub{i}"));

    public static void AddParameters(SqlCommand cmd, IReadOnlyList<string> subscriptions)
    {
        for (var i = 0; i < subscriptions.Count; i++)
            cmd.Parameters.Add(new SqlParameter($"@sub{i}", subscriptions[i]));
    }

    /// <summary>
    /// Advisor Score de la selección: promedio ponderado por pilar sobre el breakdown por
    /// suscripción del snapshot (misma mecánica del portal). Devuelve Applied=false cuando el
    /// snapshot no trae breakdown — ahí la UI avisa en vez de mostrar un número que no corresponde.
    /// Peso total cero => el pilar queda sin dato (null), que no es lo mismo que cero puntos.
    /// </summary>
    public static (IReadOnlyDictionary<int, decimal> Pillars, bool Applied) FilterScores(
        string? breakdownJson, IReadOnlyList<string> subscriptions)
    {
        var empty = (IReadOnlyDictionary<int, decimal>)new Dictionary<int, decimal>();
        if (!IsActive(subscriptions) || string.IsNullOrWhiteSpace(breakdownJson)) return (empty, false);

        List<SubscriptionScores> entries;
        try
        {
            entries = ParseBreakdown(breakdownJson!);
        }
        catch (JsonException)
        {
            return (empty, false); // breakdown corrupto: se prefiere avisar antes que inventar
        }
        if (entries.Count == 0) return (empty, false);

        var wanted = new HashSet<string>(subscriptions, StringComparer.OrdinalIgnoreCase);
        var weighted = new Dictionary<int, (decimal Sum, decimal Weight)>();
        foreach (var entry in entries)
        {
            if (!wanted.Contains(entry.SubscriptionId)) continue;
            foreach (var (pillar, score, weight) in entry.Scores)
            {
                var acc = weighted.TryGetValue(pillar, out var current) ? current : (Sum: 0m, Weight: 0m);
                weighted[pillar] = (acc.Sum + score * weight, acc.Weight + weight);
            }
        }

        var pillars = new Dictionary<int, decimal>();
        foreach (var (pillar, acc) in weighted)
            if (acc.Weight > 0) pillars[pillar] = Math.Round(acc.Sum / acc.Weight, 2);
        return (pillars, true);
    }

    private sealed record SubscriptionScores(string SubscriptionId, List<(int Pillar, decimal Score, decimal Weight)> Scores);

    private static List<SubscriptionScores> ParseBreakdown(string json)
    {
        var result = new List<SubscriptionScores>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = item.TryGetProperty("subscription_id", out var sid) && sid.ValueKind == JsonValueKind.String
                ? sid.GetString() ?? "" : "";
            if (id.Length == 0) continue;

            var scores = new List<(int, decimal, decimal)>();
            if (item.TryGetProperty("scores", out var byPillar) && byPillar.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in byPillar.EnumerateObject())
                {
                    if (!int.TryParse(prop.Name, out var pillar)) continue;
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    var score = Num(prop.Value, "score");
                    var weight = Num(prop.Value, "weight");
                    if (score is decimal s && weight is decimal w && w > 0) scores.Add((pillar, s, w));
                }
            }
            if (scores.Count > 0) result.Add(new SubscriptionScores(id, scores));
        }
        return result;
    }

    private static decimal? Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)
            ? d : null;
}
