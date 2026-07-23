using System.Net;
using OptimizacionCostos.Api.Features.Cdc.AccessReview;
using OptimizacionCostos.Api.Tests.AzureIntegration.UserSessions;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

public class AccessReviewArmClientTests
{
    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task Trae_asignaciones_de_todos_los_scopes_y_resuelve_nombre_de_rol()
    {
        var handler = new CannedHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/roleDefinitions"))
                return Json("""
                    {"value":[{"id":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/def-1",
                               "properties":{"roleName":"Contributor"}}]}
                    """);
            if (url.Contains("/roleAssignments"))
            {
                Assert.DoesNotContain("atScope()", url); // guardia: nunca volver a filtrar
                return Json("""
                    {"value":[
                      {"properties":{"principalId":"u1","principalType":"User",
                        "roleDefinitionId":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/def-1",
                        "scope":"/subscriptions/s1/resourceGroups/rg-app"}},
                      {"properties":{"principalId":"g1","principalType":"Group",
                        "roleDefinitionId":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/def-x",
                        "scope":"/subscriptions/s1"}}
                    ]}
                    """);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var svc = new AccessReviewArmClient(new FakeCredentialFactory(), new FakeHttpFactory(handler));

        var rows = await svc.GetRoleAssignmentsAsync(1, "s1");

        Assert.Equal(2, rows.Count);
        Assert.Equal("Contributor", rows[0].RoleName);
        Assert.Equal("resource_group", rows[0].ScopeLevel);
        Assert.Equal("def-x", rows[1].RoleName[^5..]); // sin definición → cae al id
        Assert.Equal("subscription", rows[1].ScopeLevel);
    }

    [Fact]
    public async Task Sigue_nextLink_en_roleAssignments()
    {
        var handler = new CannedHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/roleDefinitions"))
                return Json("""{"value":[]}""");
            if (url.Contains("/roleAssignments") && !url.Contains("skipToken"))
                return Json("""
                    {"value":[{"properties":{"principalId":"u1","principalType":"User",
                        "roleDefinitionId":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/def-1","scope":"/subscriptions/s1"}}],
                     "nextLink":"https://management.azure.com/subscriptions/s1/providers/Microsoft.Authorization/roleAssignments?api-version=2022-04-01&skipToken=abc"}
                    """);
            if (url.Contains("skipToken=abc"))
                return Json("""
                    {"value":[{"properties":{"principalId":"u2","principalType":"User",
                        "roleDefinitionId":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/def-1","scope":"/subscriptions/s1"}}]}
                    """);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var svc = new AccessReviewArmClient(new FakeCredentialFactory(), new FakeHttpFactory(handler));

        var rows = await svc.GetRoleAssignmentsAsync(1, "s1");

        Assert.Equal(2, rows.Count);
        Assert.Equal("u1", rows[0].PrincipalId);
        Assert.Equal("u2", rows[1].PrincipalId);
    }
}
