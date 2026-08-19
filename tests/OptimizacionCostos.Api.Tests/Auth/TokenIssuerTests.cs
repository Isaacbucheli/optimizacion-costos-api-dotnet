using System.Text;
using System.Text.Json;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Tests.Auth;

public class TokenIssuerTests
{
    /// <summary>Recomendación del informe DAST (WEB-12): los tokens nuevos llevan jti para
    /// trazabilidad y para habilitar revocación por sesión en el futuro. Nada lo consume
    /// todavía; este test solo fija el contrato de emisión.</summary>
    [Fact]
    public void El_token_lleva_jti_unico()
    {
        var issuer = new TokenIssuer(new AppConfig { JwtSecret = new string('s', 40) });

        var uno = Payload(issuer.Create("a@b.c", "A", "admin").AccessToken);
        var dos = Payload(issuer.Create("a@b.c", "A", "admin").AccessToken);

        Assert.True(Guid.TryParseExact(uno["jti"].GetString(), "N", out _));
        Assert.NotEqual(uno["jti"].GetString(), dos["jti"].GetString());
    }

    private static Dictionary<string, JsonElement> Payload(string token)
    {
        var b64 = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var json = Encoding.UTF8.GetString(
            Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '=')));
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }
}
