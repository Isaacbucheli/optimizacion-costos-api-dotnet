using OptimizacionCostos.Api.Features.Boletin;

namespace OptimizacionCostos.Api.Tests.Boletin;

public class AzureUpdatesFeedTests
{
    // Extracto REAL del feed (probe 2026-08-03) + un item Retirements sintético para el filtro.
    private const string Rss = """
        <?xml version="1.0" encoding="utf-8"?><rss xmlns:a10="http://www.w3.org/2005/Atom" version="2.0"><channel>
        <title>Azure service updates</title>
        <item><guid isPermaLink="false">568339</guid><link>https://azure.microsoft.com/updates?id=568339</link>
        <category>Launched</category><category>Databases</category><category>Hybrid + multicloud</category>
        <category>Azure SQL Database</category><category>Azure SQL Managed Instance</category><category>Feature</category>
        <title>[Launched] Generally Available: Immutability to the most recent seven days of backups on Azure SQL Database </title>
        <description>To enhance backup protection, &lt;p&gt;Azure SQL Database&lt;/p&gt; now automatically applies immutability.</description>
        <pubDate>Mon, 03 Aug 2026 17:00:56 Z</pubDate></item>
        <item><guid isPermaLink="false">568999</guid><link>https://azure.microsoft.com/updates?id=568999</link>
        <category>Retirements</category><category>Storage</category>
        <title>[Retirement] Something being retired</title><description>x</description>
        <pubDate>Mon, 03 Aug 2026 16:00:00 Z</pubDate></item>
        <item><guid isPermaLink="false">569001</guid><link>https://azure.microsoft.com/updates?id=569001</link>
        <category>In preview</category><category>AI + machine learning</category><category>Azure AI Foundry</category>
        <title>[In preview] Public Preview: New agent tooling</title><description>Agents.</description>
        <pubDate>Mon, 03 Aug 2026 15:00:00 Z</pubDate></item>
        </channel></rss>
        """;

    [Fact]
    public void ParseaItemsYExcluyeRetirements()
    {
        var items = AzureUpdatesFeed.Parse(Rss);
        Assert.Equal(2, items.Count);                       // el Retirements se excluye
        Assert.DoesNotContain(items, i => i.FeedGuid == "568999");
    }

    [Fact]
    public void ItemLaunchedQuedaNormalizado()
    {
        var i = AzureUpdatesFeed.Parse(Rss).Single(x => x.FeedGuid == "568339");
        Assert.Equal("launched", i.EstadoFeed);
        Assert.StartsWith("Generally Available: Immutability", i.Titulo);   // sin prefijo [Launched]
        Assert.DoesNotContain("<p>", i.Descripcion);                        // HTML aplanado
        Assert.Contains("Azure SQL Database", i.CategoriasFeed);
        Assert.Equal("resiliencia_plataforma", i.CategoriaBit);             // Databases → resiliencia
        Assert.Equal(new DateTime(2026, 8, 3, 17, 0, 56, DateTimeKind.Utc), i.PublishedAtUtc);
    }

    [Fact]
    public void IaYPreviewMapeanASusCategorias()
    {
        var i = AzureUpdatesFeed.Parse(Rss).Single(x => x.FeedGuid == "569001");
        Assert.Equal("in_preview", i.EstadoFeed);
        Assert.Equal("productividad_ia", i.CategoriaBit);
    }

    [Theory]
    [InlineData(new[] { "Security", "Azure Firewall" }, "seguridad_identidad")]
    [InlineData(new[] { "Identity" }, "seguridad_identidad")]
    [InlineData(new[] { "Management and governance" }, "costo_operacion")]
    [InlineData(new[] { "Networking", "Compute" }, "resiliencia_plataforma")]
    [InlineData(new[] { "Categoria Desconocida" }, "resiliencia_plataforma")]
    public void MapeoDeterministaDeCategorias(string[] cats, string esperado) =>
        Assert.Equal(esperado, AzureUpdatesFeed.MapCategoriaBit(cats));

    [Fact]
    public void XmlRotoNoRevientaItemsBuenos()
    {
        // item sin guid → se salta; el resto sobrevive
        var xml = Rss.Replace("<guid isPermaLink=\"false\">569001</guid>", "");
        var items = AzureUpdatesFeed.Parse(xml);
        Assert.Single(items);
    }
}
