using Azure.Core;
using Azure.Identity;
using OptimizacionCostos.Api.Features.AzureIntegration.UserSessions;

namespace OptimizacionCostos.Api.Tests.AzureIntegration.UserSessions;

public class SessionTokenCredentialTests
{
    private static SessionHandle Authenticated(AccessToken token, TokenCredential inner) =>
        new() { State = UserSessionState.Authenticated, Token = token, Inner = inner };

    [Fact]
    public async Task Token_vigente_se_devuelve_sin_llamar_al_inner()
    {
        var inner = new FakeTokenCredential(new AccessToken("no-usar", DateTimeOffset.UtcNow.AddHours(1)));
        var vigente = new AccessToken("vigente", DateTimeOffset.UtcNow.AddMinutes(30));
        var cred = new SessionTokenCredential(Authenticated(vigente, inner), "isaac@bit.ec");

        var t = await cred.GetTokenAsync(new TokenRequestContext(["https://otro.scope/.default"]), default);

        Assert.Equal("vigente", t.Token);
        Assert.Empty(inner.Requests); // no renovó
    }

    [Fact]
    public async Task Token_por_vencer_renueva_con_contexto_canonico_ignorando_el_del_caller()
    {
        var fresco = new AccessToken("fresco", DateTimeOffset.UtcNow.AddMinutes(60));
        var inner = new FakeTokenCredential(fresco);
        var porVencer = new AccessToken("viejo", DateTimeOffset.UtcNow.AddMinutes(2));
        var session = Authenticated(porVencer, inner);
        var cred = new SessionTokenCredential(session, "isaac@bit.ec");

        // El caller pide con scope raro (como hace ArmClient) — debe ignorarse.
        var t = await cred.GetTokenAsync(new TokenRequestContext(["https://management.azure.com//.default"]), default);

        Assert.Equal("fresco", t.Token);
        var ctx = Assert.Single(inner.Requests);
        Assert.Equal(["https://management.azure.com/.default"], ctx.Scopes);
        Assert.Equal("fresco", session.Token.Token); // cachea el renovado
    }

    [Fact]
    public async Task Renovacion_que_exige_interaccion_marca_expired_y_lanza()
    {
        var inner = new FakeTokenCredential(default)
        {
            OnGetToken = _ => throw new AuthenticationFailedException("interaction required"),
        };
        var session = Authenticated(new AccessToken("viejo", DateTimeOffset.UtcNow.AddMinutes(1)), inner);
        var cred = new SessionTokenCredential(session, "isaac@bit.ec");

        await Assert.ThrowsAsync<UserSessionExpiredException>(
            async () => await cred.GetTokenAsync(new TokenRequestContext(["x"]), default));
        Assert.Equal(UserSessionState.Expired, session.State);
    }

    [Fact]
    public async Task Sesion_no_autenticada_lanza()
    {
        var session = new SessionHandle { State = UserSessionState.Expired };
        var cred = new SessionTokenCredential(session, "isaac@bit.ec");
        await Assert.ThrowsAsync<UserSessionExpiredException>(
            async () => await cred.GetTokenAsync(new TokenRequestContext(["x"]), default));
    }
}
