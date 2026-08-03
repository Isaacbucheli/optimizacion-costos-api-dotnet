using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OptimizacionCostos.Api.Features.Boletin;

public sealed record FeedNovedad(
    string FeedGuid, string Titulo, string Descripcion, string Link,
    string EstadoFeed, string CategoriaBit,
    IReadOnlyList<string> CategoriasFeed, DateTime PublishedAtUtc);

/// <summary>Parser puro del RSS de Azure Updates (releasecommunications v2, validado 2026-08-03).
/// Las &lt;category&gt; mezclan estado, productCategories de Microsoft y productos concretos:
/// el mapping a las 4 categorías BIT usa solo las productCategories conocidas.</summary>
public static partial class AzureUpdatesFeed
{
    public const string RssUrl = "https://www.microsoft.com/releasecommunications/api/v2/azure/rss";

    private static readonly Dictionary<string, string> CategoriaMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AI + machine learning"] = "productividad_ia",
        ["Developer tools"] = "productividad_ia",
        ["Integration"] = "productividad_ia",
        ["Web"] = "productividad_ia",
        ["Mobile"] = "productividad_ia",
        ["Security"] = "seguridad_identidad",
        ["Identity"] = "seguridad_identidad",
        ["Management and governance"] = "costo_operacion",
        ["DevOps"] = "costo_operacion",
    };

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    public static IReadOnlyList<FeedNovedad> Parse(string rssXml)
    {
        var doc = XDocument.Parse(rssXml);
        var items = new List<FeedNovedad>();
        foreach (var item in doc.Descendants("item"))
        {
            var guid = (item.Element("guid")?.Value ?? "").Trim();
            var rawTitle = (item.Element("title")?.Value ?? "").Trim();
            if (guid.Length == 0 || rawTitle.Length == 0) continue; // item malformado: se salta
            var categories = item.Elements("category").Select(c => c.Value.Trim())
                .Where(c => c.Length > 0).ToList();
            // Los retiros ya llegan por Advisor/Service Health: acá solo novedades.
            if (categories.Any(c => c.StartsWith("Retirement", StringComparison.OrdinalIgnoreCase))) continue;

            var (estado, titulo) = ParseTitulo(rawTitle);
            var published = DateTime.TryParse(item.Element("pubDate")?.Value, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var d) ? d : DateTime.UtcNow;

            items.Add(new FeedNovedad(
                guid, titulo,
                StripHtml((item.Element("description")?.Value ?? "").Trim()),
                (item.Element("link")?.Value ?? "").Trim(),
                estado, MapCategoriaBit(categories), categories,
                DateTime.SpecifyKind(published, DateTimeKind.Utc)));
        }
        return items;
    }

    internal static string MapCategoriaBit(IReadOnlyList<string> categories)
    {
        foreach (var c in categories)
            if (CategoriaMap.TryGetValue(c, out var bit)) return bit;
        return "resiliencia_plataforma"; // default honesto: plataforma
    }

    internal static string StripHtml(string html) =>
        Regex.Replace(HtmlTagRegex().Replace(WebUtility.HtmlDecode(html), " "), @"\s+", " ").Trim();

    internal static (string EstadoFeed, string TituloLimpio) ParseTitulo(string rawTitle)
    {
        var m = Regex.Match(rawTitle, @"^\[(?<tag>[^\]]+)\]\s*(?<rest>.+)$");
        if (!m.Success) return ("otro", rawTitle);
        var tag = m.Groups["tag"].Value.Trim().ToLowerInvariant();
        var estado = tag switch { "launched" => "launched", "in preview" => "in_preview", _ => "otro" };
        return (estado, m.Groups["rest"].Value.Trim());
    }
}
