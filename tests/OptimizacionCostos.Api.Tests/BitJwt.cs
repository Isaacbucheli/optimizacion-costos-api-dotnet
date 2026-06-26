using System.Security.Cryptography;
using System.Text;

namespace OptimizacionCostos.Api.Tests;

/// <summary>
/// Construye un JWT byte-a-byte como app/auth.py::create_access_token del FastAPI:
/// header {"alg":"HS256","typ":"JWT"}, payload con claves ordenadas y separadores
/// compactos, firmado con HMAC-SHA256(JWT_SECRET). Si .NET valida ESTE token,
/// queda probado que los tokens del login actual sirven sin cambios.
/// </summary>
public static class BitJwt
{
    public static string Create(string secret, string email, string fullName, string role,
        int expiresInSeconds = 3600, long? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Claves en orden alfabetico y separadores compactos == json.dumps(sort_keys=True, separators=(",",":"))
        var header = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";
        var payload =
            $"{{\"exp\":{now + expiresInSeconds},\"iat\":{now}," +
            $"\"name\":{JsonString(fullName)},\"role\":{JsonString(role)},\"sub\":{JsonString(email)}}}";

        var unsigned = $"{B64(Encoding.UTF8.GetBytes(header))}.{B64(Encoding.UTF8.GetBytes(payload))}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = hmac.ComputeHash(Encoding.ASCII.GetBytes(unsigned));
        return $"{unsigned}.{B64(signature)}";
    }

    private static string B64(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string JsonString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
