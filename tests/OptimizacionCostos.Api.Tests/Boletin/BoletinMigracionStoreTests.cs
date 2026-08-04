using OptimizacionCostos.Api.Features.Boletin;

namespace OptimizacionCostos.Api.Tests.Boletin;

public class BoletinMigracionStoreTests
{
    [Fact]
    public void Seed_carga_y_es_valido()
    {
        var entries = BoletinMigracionStore.ReadSeedEntries();

        Assert.True(entries.Count >= 15);
        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Clave));
            Assert.False(string.IsNullOrWhiteSpace(e.Desde));
            Assert.False(string.IsNullOrWhiteSpace(e.Hacia));
            Assert.False(string.IsNullOrWhiteSpace(e.Notas));
            // El matcher compara Ordinal contra haystacks en minúsculas: un patrón con mayúsculas jamás matchearía.
            Assert.Equal(e.MatchPattern.ToLowerInvariant(), e.MatchPattern);
            if (e.LearnMoreUrl is not null)
                Assert.True(Uri.TryCreate(e.LearnMoreUrl, UriKind.Absolute, out var u)
                            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps));
        });
        Assert.Equal(entries.Count, entries.Select(e => e.Clave).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(false, false, BoletinMigracionStore.ClaveLookupOutcome.Insert)]
    [InlineData(true, true, BoletinMigracionStore.ClaveLookupOutcome.Conflict)]
    [InlineData(true, false, BoletinMigracionStore.ClaveLookupOutcome.Undelete)]
    public void DecideCreateOutcome_distingue_insert_conflicto_undelete(bool exists, bool active, BoletinMigracionStore.ClaveLookupOutcome expected)
        => Assert.Equal(expected, BoletinMigracionStore.DecideCreateOutcome(exists, active));
}
