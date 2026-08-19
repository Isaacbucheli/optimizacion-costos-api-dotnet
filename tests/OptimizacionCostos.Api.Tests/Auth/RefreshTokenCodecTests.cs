using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests.Auth;

/// <summary>
/// El refresh token es opaco: 32 bytes aleatorios en base64url. En BD solo vive su SHA-256
/// (32 bytes): si la tabla se filtrara, los hashes no sirven para autenticarse.
/// </summary>
public class RefreshTokenCodecTests
{
    [Fact]
    public void El_token_es_base64url_de_32_bytes_y_el_hash_es_estable()
    {
        var token = RefreshTokenCodec.NewToken();

        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.True(token.Length >= 43); // 32 bytes en base64url sin padding
        Assert.Equal(RefreshTokenCodec.Hash(token), RefreshTokenCodec.Hash(token));
        Assert.Equal(32, RefreshTokenCodec.Hash(token).Length);
        Assert.NotEqual(token, RefreshTokenCodec.NewToken());
    }
}
