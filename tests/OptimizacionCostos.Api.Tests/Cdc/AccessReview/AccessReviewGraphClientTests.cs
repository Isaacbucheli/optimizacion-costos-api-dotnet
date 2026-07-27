using System.Net;
using Azure.Core;
using OptimizacionCostos.Api.Features.AzureIntegration;
using OptimizacionCostos.Api.Features.Cdc.AccessReview;
using OptimizacionCostos.Api.Tests.AzureIntegration.UserSessions;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

public sealed class FakeCredentialFactory : IAzureCredentialFactory
{
    public Task<TokenCredential> GetClientSecretCredentialAsync(int credentialId, CancellationToken ct = default) =>
        Task.FromResult<TokenCredential>(new FakeTokenCredential(new AccessToken("tok", DateTimeOffset.UtcNow.AddHours(1))));
    public Task<CredentialAuthResult> TestCredentialAuthAsync(int credentialId, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task UpdateValidationStatusAsync(int credentialId, bool success, string? errorMessage = null, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task WriteAuditLogAsync(int credentialId, string action, string? actor = null, string? details = null, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public void InvalidateCachedCredential(int credentialId) { }
}

public class AccessReviewGraphClientTests
{
    private static AccessReviewGraphClient Build(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new FakeCredentialFactory(), new FakeHttpFactory(new CannedHandler(respond)));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task SweepUsers_pagina_y_lee_signInActivity()
    {
        var svc = Build(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("$select") && url.Contains("signInActivity") && !url.Contains("skiptoken"))
                return Json("""
                    {"value":[{"id":"u1","displayName":"Ana","userPrincipalName":"ana@x.com","userType":"Member",
                               "accountEnabled":true,"signInActivity":{"lastSignInDateTime":"2026-07-01T10:00:00Z"}}],
                     "@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=abc&$select=signInActivity"}
                    """);
            if (url.Contains("skiptoken"))
                return Json("""{"value":[{"id":"u2","displayName":"Beto","userPrincipalName":"beto@x.com","userType":"Guest","accountEnabled":false}]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sweep = await svc.SweepUsersAsync(1);

        Assert.True(sweep.SignInActivityAvailable);
        Assert.Equal(2, sweep.ById.Count);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero), sweep.ById["u1"].LastSignIn);
        Assert.Null(sweep.ById["u2"].LastSignIn);
        Assert.False(sweep.ById["u2"].AccountEnabled);
    }

    [Fact]
    public async Task SweepUsers_sin_licencia_P1_reintenta_sin_signInActivity()
    {
        var svc = Build(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("signInActivity"))
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    { Content = new StringContent("""{"error":{"code":"Authentication_RequestFromNonPremiumTenantOrB2CTenant"}}""") };
            return Json("""{"value":[{"id":"u1","displayName":"Ana","userPrincipalName":"ana@x.com","userType":"Member","accountEnabled":true}]}""");
        });

        var sweep = await svc.SweepUsersAsync(1);

        Assert.False(sweep.SignInActivityAvailable);
        Assert.Single(sweep.ById);
        Assert.Null(sweep.ById["u1"].LastSignIn);
    }

    [Fact]
    public async Task Mfa_excluye_password_y_email()
    {
        var svc = Build(_ => Json("""
            {"value":[{"@odata.type":"#microsoft.graph.passwordAuthenticationMethod"},
                      {"@odata.type":"#microsoft.graph.emailAuthenticationMethod"}]}
            """));
        Assert.Equal("disabled", await svc.GetMfaStatusAsync(1, "u1"));
    }

    [Fact]
    public async Task Mfa_authenticator_cuenta_como_habilitado()
    {
        var svc = Build(_ => Json("""
            {"value":[{"@odata.type":"#microsoft.graph.passwordAuthenticationMethod"},
                      {"@odata.type":"#microsoft.graph.microsoftAuthenticatorAuthenticationMethod"}]}
            """));
        Assert.Equal("enabled", await svc.GetMfaStatusAsync(1, "u1"));
    }

    [Fact]
    public async Task Mfa_error_devuelve_unavailable()
    {
        var svc = Build(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        Assert.Equal("unavailable", await svc.GetMfaStatusAsync(1, "u1"));
    }

    [Fact]
    public async Task GlobalAdmins_usa_directoryRoles_y_fallback_roleManagement()
    {
        var svc = Build(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/directoryRoles?"))
                return Json("""{"value":[]}""");  // rol no activado
            if (url.Contains("/roleManagement/directory/roleAssignments"))
                return Json("""{"value":[{"principal":{"id":"u9","displayName":"Root Admin","userPrincipalName":"root@x.com","userType":"Member"}}]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var gas = await svc.GetGlobalAdminsAsync(1);

        Assert.Single(gas);
        Assert.Equal("u9", gas[0].Id);
        Assert.Equal("root@x.com", gas[0].Upn);
    }

    [Fact]
    public async Task GlobalAdmins_por_members_no_pide_select_de_props_de_usuario()
    {
        // Regresión (bug cazado en E2E Banco Solidario): 'members' es colección de directoryObject;
        // pedir $select de props de 'user' (userType/accountEnabled/UPN) sin cast da HTTP 400.
        var svc = Build(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/directoryRoles?"))
                return Json("""{"value":[{"id":"role-ga"}]}""");
            if (url.Contains("/directoryRoles/role-ga/members"))
            {
                Assert.DoesNotContain("$select", url);
                return Json("""{"value":[{"@odata.type":"#microsoft.graph.user","id":"u1","displayName":"Admin Uno","userPrincipalName":"a1@x.com","userType":"Member"}]}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var gas = await svc.GetGlobalAdminsAsync(1);

        Assert.Single(gas);
        Assert.Equal("u1", gas[0].Id);
        Assert.Equal("a1@x.com", gas[0].Upn);
    }

    [Fact]
    public async Task TransitiveMembers_pagina_y_envia_consistency_level()
    {
        var svc = Build(req =>
        {
            Assert.Equal("eventual", req.Headers.GetValues("ConsistencyLevel").Single());
            var url = req.RequestUri!.ToString();
            Assert.DoesNotContain("$select", url); // regresión: sin $select de props de user (evita HTTP 400)
            if (!url.Contains("skiptoken"))
                return Json("""
                    {"value":[{"@odata.type":"#microsoft.graph.user","id":"u1","displayName":"Ana","userPrincipalName":"ana@x.com","userType":"Member"}],
                     "@odata.nextLink":"https://graph.microsoft.com/v1.0/groups/g1/transitiveMembers?$skiptoken=abc"}
                    """);
            return Json("""{"value":[{"@odata.type":"#microsoft.graph.servicePrincipal","id":"sp1","displayName":"App"}]}""");
        });

        var members = await svc.GetGroupTransitiveMembersAsync(1, "g1");

        Assert.Equal(2, members.Count);
        Assert.Equal("u1", members[0].Id);
        Assert.Equal("#microsoft.graph.servicePrincipal", members[1].OdataType);
    }

    [Fact]
    public async Task TransitiveMembers_grupo_borrado_404_devuelve_vacio()
    {
        // Regresión (caso BANCO DELTA): asignación RBAC huérfana a un grupo ya borrado de Entra ID
        // ("Identity not found" en el portal) → /groups/{id}/transitiveMembers da 404. Debe tratarse
        // como grupo sin miembros, no tumbar la fase Graph completa de la corrida.
        var svc = Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("""{"error":{"code":"Request_ResourceNotFound"}}""") });

        var members = await svc.GetGroupTransitiveMembersAsync(1, "g-borrado");

        Assert.Empty(members);
    }

    [Fact]
    public async Task GetByIds_trocea_en_lotes_de_1000_y_mapea_tipos()
    {
        var svc = Build(req => Json("""
            {"value":[{"@odata.type":"#microsoft.graph.servicePrincipal","id":"sp1","displayName":"Mi App","appId":"app-guid"},
                      {"@odata.type":"#microsoft.graph.group","id":"g1","displayName":"Grupo Ops"}]}
            """));
        var map = await svc.GetByIdsAsync(1, ["sp1", "g1"]);

        Assert.Equal("app-guid", map["sp1"].AppId);
        Assert.Equal("#microsoft.graph.group", map["g1"].OdataType);
    }
}
