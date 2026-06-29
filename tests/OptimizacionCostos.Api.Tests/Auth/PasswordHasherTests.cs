using OptimizacionCostos.Api.Auth;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Auth;

/// <summary>
/// Paridad byte-a-byte con app/auth.py::hash_password. Los hashes esperados se generaron
/// con el FastAPI real (PBKDF2-HMAC-SHA256, 120000 iters). Si esto pasa, los usuarios
/// existentes (hash creado por FastAPI) pueden iniciar sesión en el .NET sin re-setear clave.
/// </summary>
public sealed class PasswordHasherTests
{
    [Theory]
    // (password, salt, hashEsperado-del-Python)
    [InlineData("Secreta123!", "0123456789abcdef0123456789abcdef",
        "pbkdf2_sha256$0123456789abcdef0123456789abcdef$8be50fe9e14c2d384adf4f43f6439ec07a1aebf8cee936f3c987a16e8b1fd00b")]
    [InlineData("contraseña-áé", "deadbeefdeadbeefdeadbeefdeadbeef",
        "pbkdf2_sha256$deadbeefdeadbeefdeadbeefdeadbeef$92d74459d9cca145be9caf0427127dbab6e3a38f9a1926bbdf02743ff89dae9a")]
    public void Hash_ReproduceElHashDelFastApi(string password, string salt, string expected)
    {
        Assert.Equal(expected, PasswordHasher.Hash(password, salt));
    }

    [Theory]
    [InlineData("Secreta123!",
        "pbkdf2_sha256$0123456789abcdef0123456789abcdef$8be50fe9e14c2d384adf4f43f6439ec07a1aebf8cee936f3c987a16e8b1fd00b")]
    [InlineData("contraseña-áé",
        "pbkdf2_sha256$deadbeefdeadbeefdeadbeefdeadbeef$92d74459d9cca145be9caf0427127dbab6e3a38f9a1926bbdf02743ff89dae9a")]
    public void Verify_AceptaHashDelFastApi(string password, string storedHash)
    {
        Assert.True(PasswordHasher.Verify(password, storedHash));
    }

    [Fact]
    public void Verify_RechazaClaveIncorrecta()
    {
        var hash = PasswordHasher.Hash("correcta");
        Assert.False(PasswordHasher.Verify("incorrecta", hash));
    }

    [Fact]
    public void Verify_RechazaHashMalformadoOEsquemaDistinto()
    {
        Assert.False(PasswordHasher.Verify("x", "bcrypt$abc$def"));
        Assert.False(PasswordHasher.Verify("x", "no-dollar-signs"));
        Assert.False(PasswordHasher.Verify("x", ""));
    }

    [Fact]
    public void Hash_GeneraSaltNuevoYRoundTrips()
    {
        var h = PasswordHasher.Hash("MiClave#2026");
        Assert.StartsWith("pbkdf2_sha256$", h);
        Assert.True(PasswordHasher.Verify("MiClave#2026", h));
        // dos hashes del mismo password difieren por el salt aleatorio
        Assert.NotEqual(h, PasswordHasher.Hash("MiClave#2026"));
    }
}
