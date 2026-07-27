using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Features.AzureIntegration;
using OptimizacionCostos.Api.Features.AzureIntegration.UserSessions;

namespace OptimizacionCostos.Api.Tests.AzureIntegration;

/// <summary>
/// El factory memoiza por instancia (= por scope, = por corrida de un job). Sin eso, cada llamada a
/// ARM o Graph costaba una query a SQL + una lectura de Key Vault + un token nuevo a AAD.
/// </summary>
public class AzureCredentialFactoryCacheTests
{
    private sealed class CountingKeyVault : IKeyVaultService
    {
        public int Reads;
        public string GenerateSecretName(int clientId) => $"cred-{clientId}";
        public Task StoreSecretAsync(string n, string v, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> ReadSecretAsync(string n, CancellationToken ct = default)
        { Interlocked.Increment(ref Reads); return Task.FromResult("secreto"); }
        public Task DeleteSecretAsync(string n, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FailingKeyVault : IKeyVaultService
    {
        public int Reads;
        public bool Fail = true;
        public string GenerateSecretName(int clientId) => $"cred-{clientId}";
        public Task StoreSecretAsync(string n, string v, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> ReadSecretAsync(string n, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            return Fail ? throw new InvalidOperationException("Key Vault caído") : Task.FromResult("secreto");
        }
        public Task DeleteSecretAsync(string n, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoSessions : IAzureUserSessionService
    {
        public int Resolutions;
        public UserSessionSnapshot Start(string email) => throw new NotSupportedException();
        public UserSessionSnapshot? GetStatus(string email) => null;
        public void Disconnect(string email) { }
        public TokenCredential GetCredentialForEmail(string email)
        { Interlocked.Increment(ref Resolutions); return new FakeToken(); }
    }

    private sealed class FakeToken : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => default;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => default;
    }

    /// <summary>Subclase de test: reemplaza la lectura en SQL (seam `protected virtual`, mismo patrón
    /// que TestableSyncService) y cuenta cuántas veces se consultó la fila.</summary>
    private sealed class TestableFactory(IKeyVaultService keyVault, IAzureUserSessionService sessions, string authType)
        : SqlAzureCredentialFactory(null!, keyVault, sessions, NullLogger<SqlAzureCredentialFactory>.Instance)
    {
        public int Fetches;
        protected override Task<CredentialRow> FetchCredentialRowAsync(int credentialId, CancellationToken ct)
        {
            Interlocked.Increment(ref Fetches);
            return Task.FromResult(new CredentialRow(credentialId, 1, "cred", "tenant", "app",
                $"cred-{credentialId}", authType, "dueno@bit.ec"));
        }
    }

    [Fact]
    public async Task Reusa_la_credencial_dentro_del_mismo_scope()
    {
        var kv = new CountingKeyVault();
        var factory = new TestableFactory(kv, new NoSessions(), "app_secret");

        var first = await factory.GetClientSecretCredentialAsync(7);
        for (var i = 0; i < 50; i++) Assert.Same(first, await factory.GetClientSecretCredentialAsync(7));

        Assert.Equal(1, factory.Fetches);
        Assert.Equal(1, kv.Reads);
    }

    [Fact]
    public async Task Llamadas_concurrentes_construyen_una_sola_vez()
    {
        // Es el caso real: el prefetch de MFA pide la credencial desde 8 hilos a la vez.
        var kv = new CountingKeyVault();
        var factory = new TestableFactory(kv, new NoSessions(), "app_secret");

        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => factory.GetClientSecretCredentialAsync(7)));

        Assert.Equal(1, factory.Fetches);
        Assert.Equal(1, kv.Reads);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task Credenciales_distintas_no_se_confunden()
    {
        var kv = new CountingKeyVault();
        var factory = new TestableFactory(kv, new NoSessions(), "app_secret");

        var a = await factory.GetClientSecretCredentialAsync(1);
        var b = await factory.GetClientSecretCredentialAsync(2);

        Assert.NotSame(a, b);
        Assert.Equal(2, kv.Reads);
    }

    [Fact]
    public async Task Invalidar_fuerza_a_releer_el_secreto()
    {
        // Lo que usa la rotación de secreto: tras rotar, la credencial memoizada tendría el viejo.
        var kv = new CountingKeyVault();
        var factory = new TestableFactory(kv, new NoSessions(), "app_secret");

        var first = await factory.GetClientSecretCredentialAsync(7);
        factory.InvalidateCachedCredential(7);
        var second = await factory.GetClientSecretCredentialAsync(7);

        Assert.NotSame(first, second);
        Assert.Equal(2, kv.Reads);
        Assert.Equal(2, factory.Fetches);
    }

    [Fact]
    public async Task Un_fallo_de_key_vault_no_queda_cacheado()
    {
        // Si el fallo quedara pegado, un error transitorio arruinaría el resto de la corrida.
        var kv = new FailingKeyVault();
        var factory = new TestableFactory(kv, new NoSessions(), "app_secret");

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetClientSecretCredentialAsync(7));

        kv.Fail = false;
        Assert.NotNull(await factory.GetClientSecretCredentialAsync(7));
        Assert.Equal(2, kv.Reads);
    }

    [Fact]
    public async Task La_sesion_de_usuario_se_resuelve_en_cada_llamada()
    {
        // Lighthouse: la sesión puede expirar durante la corrida, así que NO se memoiza.
        var sessions = new NoSessions();
        var factory = new TestableFactory(new CountingKeyVault(), sessions, "user_session");

        for (var i = 0; i < 5; i++) await factory.GetClientSecretCredentialAsync(7);

        Assert.Equal(5, sessions.Resolutions);
        // La fila sí se cachea: es lectura de SQL, no de la sesión.
        Assert.Equal(1, factory.Fetches);
    }
}
