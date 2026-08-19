using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Auth;

/// <summary>
/// Emisión de JWT. Port 1:1 de app/auth.py::create_access_token. Produce el MISMO formato
/// que el FastAPI (HS256 manual, firmado con JWT_SECRET, base64url sin padding), que el
/// validador JwtBearer del .NET (AuthSetup) ya acepta. Claims: sub, name, role, iat, exp.
/// </summary>
public sealed class TokenIssuer(AppConfig config)
{
    public sealed record IssuedToken(string AccessToken, int ExpiresInSeconds);

    public IssuedToken Create(string email, string fullName, string role)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiresIn = config.AuthTokenMinutes * 60;

        var header = new Dictionary<string, object> { ["alg"] = "HS256", ["typ"] = "JWT" };
        var payload = new Dictionary<string, object>
        {
            ["sub"] = email,
            ["name"] = fullName,
            ["role"] = role,
            ["iat"] = now,
            ["exp"] = now + expiresIn,
            // Identificador de sesión (informe DAST, WEB-12): hoy solo trazabilidad;
            // habilita revocación por sesión individual en el futuro. La revocación
            // vigente compara iat contra app_users.tokens_revoked_at.
            ["jti"] = Guid.NewGuid().ToString("N"),
        };

        var unsigned = $"{B64Url(JsonBytes(header))}.{B64Url(JsonBytes(payload))}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(config.JwtSecret));
        var signature = hmac.ComputeHash(Encoding.ASCII.GetBytes(unsigned));
        return new IssuedToken($"{unsigned}.{B64Url(signature)}", expiresIn);
    }

    private static byte[] JsonBytes(Dictionary<string, object> value) =>
        JsonSerializer.SerializeToUtf8Bytes(value); // compacto, sin espacios

    private static string B64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
