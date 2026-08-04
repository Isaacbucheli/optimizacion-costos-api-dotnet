namespace OptimizacionCostos.Api.Features.Boletin;

public sealed record AnuncioSinRuta(string Title, string? Summary, string? RecommendedAction);

/// <summary>Cruce AL LEER del catálogo de rutas de migración contra los avisos vigentes del cliente.
/// Puro y testeable. La ruta es propiedad GLOBAL del anuncio: acá solo se decide cuáles rutas le
/// aplican a este cliente y con cuántos recursos. Nada se persiste (spec E4 §1, enfoque A).</summary>
public static class BoletinMigracion
{
    /// <summary>eol → RetiringFeature (ahí vive el producto del catálogo lifecycle, p. ej.
    /// "Windows Server 2012 R2"); retiros → Title + RetiringFeature (los títulos de Advisor/Service
    /// Health nombran lo que se retira). Minúsculas porque match_pattern se normaliza así en CRUD y seed.</summary>
    internal static string MatchText(StoredRetirement first) =>
        (first.Source == RetirementRow.SourceEol
            ? first.RetiringFeature
            : first.Title + " " + first.RetiringFeature).ToLowerInvariant();

    /// <summary>Gana el patrón MÁS LARGO (desambigua "windows server 2012" vs "... r2");
    /// misma mecánica probada de BoletinEol.MatchResources.</summary>
    internal static MigracionEntry? BestRoute(IReadOnlyList<MigracionEntry> entries, string matchText) =>
        entries.Where(e => e.IsActive && matchText.Contains(e.MatchPattern, StringComparison.Ordinal))
               .OrderByDescending(e => e.MatchPattern.Length)
               .FirstOrDefault();

    public static IReadOnlyDictionary<string, object?> BuildSection(
        IReadOnlyList<StoredRetirement> rows, IReadOnlyList<MigracionEntry> entries, DateOnly today)
    {
        var porRuta = new Dictionary<int, (MigracionEntry Ruta, List<Dictionary<string, object?>> Anuncios, int Recursos)>();
        var sinRuta = new List<Dictionary<string, object?>>();

        foreach (var g in rows.GroupBy(r => (r.Source, r.AnnouncementKey)))
        {
            var first = g.First();
            var resourceCount = g.Count(r => r.AzureResourceId is not null);
            var anuncio = new Dictionary<string, object?>
            {
                ["source"] = first.Source,
                ["announcement_key"] = first.AnnouncementKey,
                ["title"] = first.Title,
                ["title_es"] = g.Select(r => r.TitleEs).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? first.Title,
                ["retirement_date"] = first.RetirementDate?.ToString("yyyy-MM-dd"),
                ["urgency"] = BoletinUrgency.Classify(first.RetirementDate, today),
                ["resource_count"] = resourceCount,
            };

            var best = BestRoute(entries, MatchText(first));
            if (best is null) { sinRuta.Add(anuncio); continue; }

            if (!porRuta.TryGetValue(best.Id, out var acc)) acc = (best, [], 0);
            acc.Anuncios.Add(anuncio);
            porRuta[best.Id] = (acc.Ruta, acc.Anuncios, acc.Recursos + resourceCount);
        }

        static string? Nearest(List<Dictionary<string, object?>> anuncios) =>
            anuncios.Select(a => a["retirement_date"] as string).Where(d => d is not null)
                    .OrderBy(d => d, StringComparer.Ordinal).FirstOrDefault();

        var rutas = porRuta.Values
            .Select(v => new Dictionary<string, object?>
            {
                ["id"] = v.Ruta.Id, ["clave"] = v.Ruta.Clave,
                ["desde"] = v.Ruta.Desde, ["hacia"] = v.Ruta.Hacia,
                ["notas"] = v.Ruta.Notas, ["learn_more_url"] = v.Ruta.LearnMoreUrl,
                ["announcements"] = OrderAnuncios(v.Anuncios),
                ["nearest_date"] = Nearest(v.Anuncios),
                ["total_resources"] = v.Recursos,
            })
            .OrderBy(r => r["nearest_date"] is null)
            .ThenBy(r => r["nearest_date"] as string, StringComparer.Ordinal)
            .ToList();

        return new Dictionary<string, object?> { ["rutas"] = rutas, ["sin_ruta"] = OrderAnuncios(sinRuta) };
    }

    private static List<Dictionary<string, object?>> OrderAnuncios(List<Dictionary<string, object?>> anuncios) =>
        anuncios.OrderBy(a => a["retirement_date"] is null)
                .ThenBy(a => a["retirement_date"] as string, StringComparer.Ordinal).ToList();

    public static IReadOnlyList<AnuncioSinRuta> SinRuta(
        IReadOnlyList<StoredRetirement> rows, IReadOnlyList<MigracionEntry> entries) =>
        rows.GroupBy(r => (r.Source, r.AnnouncementKey))
            .Where(g => BestRoute(entries, MatchText(g.First())) is null)
            .Select(g => new AnuncioSinRuta(
                g.First().Title,
                g.Select(r => r.Summary).FirstOrDefault(s => !string.IsNullOrEmpty(s)),
                g.Select(r => r.RecommendedAction).FirstOrDefault(s => !string.IsNullOrEmpty(s))))
            .ToList();
}
