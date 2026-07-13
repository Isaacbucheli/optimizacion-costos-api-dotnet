using OptimizacionCostos.Api.Auth;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Auth;

/// <summary>
/// Compatibilidad y endurecimiento del hasher:
///   - Verify DEBE aceptar los hashes legacy del stack anterior (PBKDF2-SHA256, 120000 iters,
///     formato de 3 partes; vectores generados con el FastAPI real) para que los usuarios
///     existentes sigan iniciando sesión sin re-setear clave.
///   - Hash emite el formato nuevo con iteraciones embebidas (600000, OWASP) y NeedsRehash
///     marca los hashes débiles para el re-hash transparente en el login.
/// </summary>
public sealed class PasswordHasherTests
{
    private const string LegacyHash1 =
        "pbkdf2_sha256$0123456789abcdef0123456789abcdef$8be50fe9e14c2d384adf4f43f6439ec07a1aebf8cee936f3c987a16e8b1fd00b";
    private const string LegacyHash2 =
        "pbkdf2_sha256$deadbeefdeadbeefdeadbeefdeadbeef$92d74459d9cca145be9caf0427127dbab6e3a38f9a1926bbdf02743ff89dae9a";

    // ---- Compatibilidad con hashes legacy (vectores del FastAPI real) ----

    [Theory]
    [InlineData("Secreta123!", LegacyHash1)]
    [InlineData("contraseña-áé", LegacyHash2)]
    public void Verify_AceptaHashLegacyDelFastApi(string password, string storedHash)
    {
        Assert.True(PasswordHasher.Verify(password, storedHash));
    }

    [Theory]
    [InlineData("otra-clave", LegacyHash1)]
    [InlineData("Secreta123", LegacyHash1)] // sin el '!' final
    public void Verify_RechazaClaveIncorrectaContraHashLegacy(string password, string storedHash)
    {
        Assert.False(PasswordHasher.Verify(password, storedHash));
    }

    // ---- Formato nuevo (iteraciones embebidas) ----

    [Fact]
    public void Hash_EmiteFormatoNuevoConIteracionesOwaspYRoundTrips()
    {
        var h = PasswordHasher.Hash("MiClave#2026");
        Assert.StartsWith($"pbkdf2_sha256${PasswordHasher.Iterations}$", h);
        Assert.Equal(4, h.Split('$').Length);
        Assert.True(PasswordHasher.Verify("MiClave#2026", h));
        Assert.False(PasswordHasher.Verify("otra", h));
        // dos hashes del mismo password difieren por el salt aleatorio
        Assert.NotEqual(h, PasswordHasher.Hash("MiClave#2026"));
    }

    [Fact]
    public void Verify_RechazaHashMalformadoOEsquemaDistinto()
    {
        Assert.False(PasswordHasher.Verify("x", "bcrypt$abc$def"));
        Assert.False(PasswordHasher.Verify("x", "no-dollar-signs"));
        Assert.False(PasswordHasher.Verify("x", ""));
        Assert.False(PasswordHasher.Verify("x", null));
        Assert.False(PasswordHasher.Verify("x", "pbkdf2_sha256$$")); // partes vacías
        Assert.False(PasswordHasher.Verify("x", "pbkdf2_sha256$no-numero$salt$digest"));
        // iteraciones absurdas (hash manipulado): se rechaza sin derivar
        Assert.False(PasswordHasher.Verify("x", "pbkdf2_sha256$99999999$salt$digest"));
        Assert.False(PasswordHasher.Verify("x", "pbkdf2_sha256$0$salt$digest"));
    }

    // ---- NeedsRehash (re-hash transparente en el login) ----

    [Fact]
    public void NeedsRehash_MarcaLegacyYDebiles_NoElFormatoActual()
    {
        Assert.True(PasswordHasher.NeedsRehash(LegacyHash1)); // legacy 120k
        Assert.True(PasswordHasher.NeedsRehash("pbkdf2_sha256$120000$abc$def")); // nuevo pero débil
        Assert.False(PasswordHasher.NeedsRehash(PasswordHasher.Hash("clave-fuerte-1"))); // actual
        // malformados: no son re-hasheables (Verify ya los rechaza)
        Assert.False(PasswordHasher.NeedsRehash(null));
        Assert.False(PasswordHasher.NeedsRehash(""));
        Assert.False(PasswordHasher.NeedsRehash("bcrypt$abc$def"));
    }
}
