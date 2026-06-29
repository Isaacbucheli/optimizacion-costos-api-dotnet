using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.AzureIntegration;
using Xunit;

namespace OptimizacionCostos.Api.Tests.AzureIntegration;

/// <summary>
/// Tests de la parte determinista de la capa de integración Azure (B1) que NO requiere Azure:
/// el formato del nombre de secreto. KV/credenciales/Resource Graph se validan con tests de
/// integración gateados (necesitan KV real + fila de credencial + tenant), igual que el resto.
/// </summary>
public sealed class KeyVaultServiceTests
{
    private static KeyVaultService NewService() =>
        new(new AppConfig { KeyVaultUrl = "https://kv-test.vault.azure.net/" },
            NullLogger<KeyVaultService>.Instance);

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99999)]
    public void GenerateSecretName_FormatoYReglasKeyVault(int clientId)
    {
        var name = NewService().GenerateSecretName(clientId);

        // Formato: cred-{clientId}-{16 hex}  (paridad con app/keyvault.py).
        Assert.Matches(new Regex($"^cred-{clientId}-[0-9a-f]{{16}}$"), name);
        // Regla Key Vault: solo letras, números y guiones; máx 127 chars.
        Assert.True(name.Length <= 127);
        Assert.Matches(new Regex("^[A-Za-z0-9-]+$"), name);
    }

    [Fact]
    public void GenerateSecretName_EsUnicoEntreLlamadas()
    {
        var svc = NewService();
        var a = svc.GenerateSecretName(7);
        var b = svc.GenerateSecretName(7);
        Assert.NotEqual(a, b); // el sufijo aleatorio evita colisiones
    }
}
