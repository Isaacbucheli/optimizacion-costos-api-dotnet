using OptimizacionCostos.Api.Features.Boletin;

namespace OptimizacionCostos.Api.Tests.Boletin;

public class BoletinLifecycleStoreTests
{
    [Fact]
    public void ElSeedEmbebidoParseaYTieneEntradasValidas()
    {
        var entries = BoletinLifecycleStore.ReadSeedEntries();
        Assert.True(entries.Count >= 10);
        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Clave));
            Assert.False(string.IsNullOrWhiteSpace(e.Producto));
            Assert.Contains(e.Categoria, new[] { "so", "bd" });
            Assert.Contains(e.MatchField, new[] { "os_name", "sql_image_offer" });
            Assert.False(string.IsNullOrWhiteSpace(e.MatchPattern));
            Assert.Equal(e.MatchPattern, e.MatchPattern.ToLowerInvariant()); // patrones en minúsculas
            Assert.InRange(e.EndOfSupport.Year, 2019, 2035);
            Assert.False(string.IsNullOrWhiteSpace(e.Recomendacion));
        });
        Assert.Equal(entries.Count, entries.Select(e => e.Clave).Distinct().Count());
        // Desambiguación 2012 vs 2012 R2 presente (la regla "patrón más largo gana" depende de ambas)
        Assert.Contains(entries, e => e.Clave == "windows-server-2012");
        Assert.Contains(entries, e => e.Clave == "windows-server-2012-r2");
    }

    [Fact]
    public void LaWhitelistNoIncluyeColumnasDeSistema()
    {
        Assert.DoesNotContain("id", LifecycleColumns.Editable);
        Assert.DoesNotContain("created_at", LifecycleColumns.Editable);
        Assert.Contains("match_pattern", LifecycleColumns.Editable);
        Assert.Contains("end_of_support", LifecycleColumns.Editable);
    }
}
