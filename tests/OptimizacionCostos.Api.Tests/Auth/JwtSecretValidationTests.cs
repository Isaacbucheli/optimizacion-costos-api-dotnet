using Microsoft.Extensions.DependencyInjection;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Tests.Auth;

/// <summary>
/// El JWT que el ZAP del 2026-08-03 publicó con su firma habilita atacar el secreto fuera de línea,
/// y hasta ahora nada verificaba que el secreto tuviera entropía suficiente. Medido en la práctica:
/// SymmetricSecurityKey acepta una llave de 4 bytes (KeySize=32 bits) y HMACSHA256 acepta incluso una
/// vacía, así que un JWT_SECRET corto o mal escrito dejaba a la API arrancando normal y firmando
/// tokens triviales de romper. Estas pruebas fijan que no arranque.
/// </summary>
public sealed class JwtSecretValidationTests
{
    private static AppConfig ConSecreto(string secret) => new() { JwtSecret = secret };

    private static void Configurar(string secret) =>
        new ServiceCollection().AddBitJwtAuth(ConSecreto(secret));

    [Theory]
    [InlineData("")]                                    // variable ausente o mal escrita
    [InlineData("abcd")]                                // 4 bytes: antes se aceptaba
    [InlineData("secreto")]                             // 7 bytes
    [InlineData("0123456789012345678901234567890")]     // 31 bytes: justo por debajo
    public void Un_secreto_mas_corto_que_el_minimo_impide_arrancar(string secret)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Configurar(secret));
        Assert.Contains("JWT_SECRET", ex.Message);
        Assert.Contains(AuthSetup.MinSecretBytes.ToString(), ex.Message);
    }

    [Theory]
    [InlineData("01234567890123456789012345678901")]    // 32 bytes exactos: el borde pasa
    [InlineData("test-secret-con-mas-de-32-caracteres-1234567890")]
    public void Un_secreto_del_minimo_o_mayor_arranca(string secret)
    {
        Assert.Equal(32, "01234567890123456789012345678901".Length);
        Configurar(secret); // no lanza
    }

    [Theory]
    // App Service entrega la referencia LITERAL cuando no la puede resolver. Mide más de 32 bytes,
    // así que pasaría el chequeo de largo y la API arrancaría firmando con el texto de la
    // referencia: /health en 200, todos los tokens inválidos y nada en los logs.
    [InlineData("@Microsoft.KeyVault(SecretUri=https://kv-ejemplo.vault.azure.net/secrets/jwt-secret/)")]
    [InlineData("@Microsoft.KeyVault(VaultName=kv-ejemplo;SecretName=jwt-secret)")]
    [InlineData("@microsoft.keyvault(secreturi=https://kv-ejemplo.vault.azure.net/secrets/x/)")]
    public void Una_referencia_a_key_vault_sin_resolver_impide_arrancar(string secret)
    {
        // Precondición del caso: es larga, o sea que el chequeo de largo no la atraparía.
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(secret) > AuthSetup.MinSecretBytes);

        var ex = Assert.Throws<InvalidOperationException>(() => Configurar(secret));
        Assert.Contains("Key Vault", ex.Message);
        // El mensaje debe mandar a revisar el vault, no a cambiar el largo del secreto.
        Assert.DoesNotContain("bytes", ex.Message);
    }

    [Fact]
    public void El_minimo_es_el_tamano_de_salida_de_SHA256()
    {
        // RFC 7518 3.2: la llave de un HMAC debe medir al menos lo que la salida del hash.
        Assert.Equal(32, AuthSetup.MinSecretBytes);
    }

    [Fact]
    public void Se_mide_en_bytes_no_en_caracteres()
    {
        // 20 caracteres acentuados son 40 bytes en UTF-8, y lo que alimenta al HMAC son los bytes.
        // Al revés también importa: un secreto de 31 caracteres ASCII no alcanza aunque "parezca"
        // largo, y este par de casos deja fijada la unidad de medida.
        var acentuado = new string('á', 20);
        Assert.Equal(20, acentuado.Length);
        Assert.Equal(40, System.Text.Encoding.UTF8.GetByteCount(acentuado));
        Configurar(acentuado); // no lanza: 40 bytes

        Assert.Throws<InvalidOperationException>(() => Configurar(new string('a', 31)));
    }
}
