using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests.Auth;

/// <summary>
/// WEB-12 (informe BIT-TEST-DAST v1.2): un JWT emitido antes de la revocación (logout,
/// cambio de contraseña) debe rechazarse en TODOS los endpoints protegidos, aunque su
/// firma y su exp sigan siendo válidos. La marca vive en app_users.tokens_revoked_at y
/// se compara contra el iat del token, truncado a segundos, con desigualdad ESTRICTA:
/// un token emitido en el mismo segundo de la revocación sobrevive (es lo que permite
/// que change-password revoque y emita el reemplazo en la misma llamada).
/// </summary>
public sealed class TokenRevocationTests : IClassFixture<TokenRevocationTests.Factory>
{
    private readonly Factory _factory;
    public TokenRevocationTests(Factory factory) => _factory = factory;

    private HttpClient ClientWithToken(string email, long iatUnix)
    {
        var client = _factory.CreateClient();
        var token = BitJwt.Create(Factory.Secret, email, "Usuario QA", "consultor",
            expiresInSeconds: 28_800, nowOverride: iatUnix);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Token_emitido_antes_de_la_revocacion_devuelve_401()
    {
        var revokedAt = DateTime.UtcNow;
        _factory.Directory.Add("qa.revocado@bit.test", "consultor", revokedAt: revokedAt);
        var iatViejo = new DateTimeOffset(revokedAt).ToUnixTimeSeconds() - 600; // 10 min antes

        var res = await ClientWithToken("qa.revocado@bit.test", iatViejo).GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Token_emitido_despues_de_la_revocacion_entra()
    {
        var revokedAt = DateTime.UtcNow.AddMinutes(-10);
        _factory.Directory.Add("qa.relogin@bit.test", "consultor", revokedAt: revokedAt);
        var iatNuevo = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();

        var res = await ClientWithToken("qa.relogin@bit.test", iatNuevo).GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Mismo_segundo_de_la_revocacion_sobrevive()
    {
        // Desigualdad estricta: iat == floor(revoked_at) NO se rechaza (flujo change-password).
        var revokedAt = DateTime.UtcNow;
        _factory.Directory.Add("qa.mismo.segundo@bit.test", "consultor", revokedAt: revokedAt);
        var iatMismoSegundo = new DateTimeOffset(revokedAt).ToUnixTimeSeconds();

        var res = await ClientWithToken("qa.mismo.segundo@bit.test", iatMismoSegundo).GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Sin_marca_de_revocacion_todo_sigue_igual()
    {
        _factory.Directory.Add("qa.sin.marca@bit.test", "consultor");
        var iat = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds() - 3600;

        var res = await ClientWithToken("qa.sin.marca@bit.test", iat).GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IAppUserStore>();
                services.AddSingleton<IAppUserStore>(new FakeAppUserStore());
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
            });
        }
    }
}
