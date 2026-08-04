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

    // ---- DecideCreateOutcome: mismo patrón de BoletinLifecycleStoreTests (Facts, no Theory —
    // ClaveLookupOutcome es internal; exponerlo como parámetro de un método público de test
    // (InlineData) viola CS0051 aunque InternalsVisibleTo cubra el acceso al tipo) ----

    [Fact]
    public void DecideCreateOutcome_claveNueva_decideInsert()
        => Assert.Equal(
            BoletinMigracionStore.ClaveLookupOutcome.Insert,
            BoletinMigracionStore.DecideCreateOutcome(claveExists: false, existingIsActive: false));

    [Fact]
    public void DecideCreateOutcome_claveExistenteActiva_decideConflict()
        => Assert.Equal(
            BoletinMigracionStore.ClaveLookupOutcome.Conflict,
            BoletinMigracionStore.DecideCreateOutcome(claveExists: true, existingIsActive: true));

    [Fact]
    public void DecideCreateOutcome_claveExistenteDesactivada_decideUndelete()
        => Assert.Equal(
            BoletinMigracionStore.ClaveLookupOutcome.Undelete,
            BoletinMigracionStore.DecideCreateOutcome(claveExists: true, existingIsActive: false));
}
