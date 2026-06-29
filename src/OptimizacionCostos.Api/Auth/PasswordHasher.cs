using System.Security.Cryptography;
using System.Text;

namespace OptimizacionCostos.Api.Auth;

/// <summary>
/// Hashing/verificación de contraseñas. Port 1:1 de hash_password/verify_password de
/// app/auth.py. DEBE ser byte-compatible para que los usuarios existentes (hash creado
/// por el FastAPI) puedan iniciar sesión en el .NET.
///
/// Esquema: <c>pbkdf2_sha256${salt}${digestHex}</c>
///   - PBKDF2-HMAC-SHA256, 120000 iteraciones.
///   - salt = 16 bytes aleatorios → 32 hex (secrets.token_hex(16) en Python).
///   - password en UTF-8; salt usado como bytes ASCII (su representación hex).
///   - digest = 32 bytes (longitud de SHA-256) → hex en minúsculas.
/// </summary>
public static class PasswordHasher
{
    private const string Algorithm = "pbkdf2_sha256";
    private const int Iterations = 120_000;
    private const int DigestLength = 32; // SHA-256

    /// <summary>Genera el hash. Si <paramref name="salt"/> es null, crea uno nuevo (16 bytes → 32 hex).</summary>
    public static string Hash(string password, string? salt = null)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password is required", nameof(password));

        salt ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var digest = Rfc2898DeriveBytes.Pbkdf2(
            password: Encoding.UTF8.GetBytes(password),
            salt: Encoding.ASCII.GetBytes(salt),
            iterations: Iterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: DigestLength);

        return $"{Algorithm}${salt}${Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    /// <summary>Verifica una contraseña contra un hash almacenado. Comparación en tiempo constante.</summary>
    public static bool Verify(string password, string? storedHash)
    {
        try
        {
            if (string.IsNullOrEmpty(storedHash)) return false;
            var parts = storedHash.Split('$', 3);
            if (parts.Length != 3 || parts[0] != Algorithm) return false;

            var salt = parts[1];
            var expected = Hash(password, salt);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(storedHash));
        }
        catch
        {
            return false;
        }
    }
}
