using System.Security.Cryptography;
using System.Text;

namespace OptimizacionCostos.Api.Auth;

/// <summary>
/// Hashing/verificación de contraseñas (PBKDF2-HMAC-SHA256).
///
/// Formatos soportados:
///   - Nuevo:   <c>pbkdf2_sha256${iteraciones}${salt}${digestHex}</c> — lo que emite <see cref="Hash"/>,
///     con <see cref="Iterations"/> iteraciones (recomendación OWASP para PBKDF2-SHA256).
///   - Legacy:  <c>pbkdf2_sha256${salt}${digestHex}</c> — hashes creados por el stack anterior
///     (FastAPI, 120000 iteraciones fijas). Solo se aceptan en <see cref="Verify"/>; el login
///     los re-hashea al formato nuevo cuando la contraseña valida (<see cref="NeedsRehash"/>).
///
/// En ambos: salt = 16 bytes aleatorios → 32 hex; password en UTF-8; el salt se usa como
/// bytes ASCII de su representación hex; digest = 32 bytes (SHA-256) → hex en minúsculas.
/// </summary>
public static class PasswordHasher
{
    private const string Algorithm = "pbkdf2_sha256";

    /// <summary>Iteraciones para hashes nuevos (OWASP 2023+ recomienda ≥600k para PBKDF2-SHA256).</summary>
    public const int Iterations = 600_000;

    // Hashes legacy (formato de 3 partes, sin iteraciones embebidas): siempre 120k.
    private const int LegacyIterations = 120_000;
    private const int DigestLength = 32; // SHA-256
    // Cota de sanidad al parsear iteraciones de la BD (evita DoS con un hash manipulado).
    private const int MaxIterations = 5_000_000;

    /// <summary>Genera el hash en formato nuevo. Si <paramref name="salt"/> es null, crea uno (16 bytes → 32 hex).</summary>
    public static string Hash(string password, string? salt = null)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password is required", nameof(password));

        salt ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var digestHex = Derive(password, salt, Iterations);
        return $"{Algorithm}${Iterations}${salt}${digestHex}";
    }

    /// <summary>Verifica una contraseña contra un hash almacenado (nuevo o legacy). Comparación en tiempo constante.</summary>
    public static bool Verify(string password, string? storedHash)
    {
        try
        {
            if (string.IsNullOrEmpty(password)) return false;
            if (!TryParse(storedHash, out var iterations, out var salt, out var digestHex)) return false;

            var expected = Derive(password, salt, iterations);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(digestHex));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// true si el hash es parseable pero usa parámetros más débiles que los actuales (formato
    /// legacy o menos iteraciones). El llamador debe re-hashear tras validar la contraseña.
    /// </summary>
    public static bool NeedsRehash(string? storedHash) =>
        TryParse(storedHash, out var iterations, out _, out _) && iterations < Iterations;

    private static string Derive(string password, string salt, int iterations)
    {
        var digest = Rfc2898DeriveBytes.Pbkdf2(
            password: Encoding.UTF8.GetBytes(password),
            salt: Encoding.ASCII.GetBytes(salt),
            iterations: iterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: DigestLength);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static bool TryParse(string? storedHash, out int iterations, out string salt, out string digestHex)
    {
        iterations = 0; salt = ""; digestHex = "";
        if (string.IsNullOrEmpty(storedHash)) return false;

        var parts = storedHash.Split('$');
        if (parts[0] != Algorithm) return false;

        if (parts.Length == 3) // legacy: pbkdf2_sha256$salt$digest
        {
            iterations = LegacyIterations;
            salt = parts[1]; digestHex = parts[2];
        }
        else if (parts.Length == 4 // nuevo: pbkdf2_sha256$iteraciones$salt$digest
            && int.TryParse(parts[1], out iterations)
            && iterations >= 1 && iterations <= MaxIterations)
        {
            salt = parts[2]; digestHex = parts[3];
        }
        else
        {
            iterations = 0;
            return false;
        }
        return salt.Length > 0 && digestHex.Length > 0;
    }
}
