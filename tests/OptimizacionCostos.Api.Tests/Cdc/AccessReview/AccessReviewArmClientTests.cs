using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Features.Cdc.AccessReview;
using OptimizacionCostos.Api.Tests.AzureIntegration.UserSessions;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

public class AccessReviewArmClientTests
{
    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static AccessReviewArmClient Client(CannedHandler handler) =>
        new(new FakeCredentialFactory(), new FakeHttpFactory(handler),
            NullLogger<AccessReviewArmClient>.Instance);

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

        var rows = await Client(handler).GetRoleAssignmentsAsync(1, "s1");

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

        var rows = await Client(handler).GetRoleAssignmentsAsync(1, "s1");

        Assert.Equal(2, rows.Count);
        Assert.Equal("u1", rows[0].PrincipalId);
        Assert.Equal("u2", rows[1].PrincipalId);
    }

    [Fact]
    public async Task Conserva_principal_types_no_estandar()
    {
        // Antes se descartaban en silencio: un ForeignGroup con Owner (grupo administrado desde otro
        // tenant, típico de MSP) quedaba invisible y el total no cuadraba con el portal.
        var handler = new CannedHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/roleDefinitions"))
                return Json("""
                    {"value":[{"id":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/def-1",
                               "properties":{"roleName":"Owner","type":"BuiltInRole",
                                 "permissions":[{"actions":["*"],"notActions":[]}]}}]}
                    """);
            if (url.Contains("/roleAssignments"))
                return Json("""
                    {"value":[
                      {"properties":{"principalId":"u1","principalType":"User",
                        "roleDefinitionId":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/def-1","scope":"/subscriptions/s1"}},
                      {"properties":{"principalId":"fg1","principalType":"ForeignGroup",
                        "roleDefinitionId":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/def-1","scope":"/subscriptions/s1"}},
                      {"properties":{"principalId":"d1","principalType":"Device",
                        "roleDefinitionId":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/def-1","scope":"/subscriptions/s1"}},
                      {"properties":{"principalId":"x1",
                        "roleDefinitionId":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/def-1","scope":"/subscriptions/s1"}}
                    ]}
                    """);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var rows = await Client(handler).GetRoleAssignmentsAsync(1, "s1");

        Assert.Equal(4, rows.Count);
        Assert.Equal(["User", "ForeignGroup", "Device", "Unknown"], rows.Select(r => r.PrincipalType));
    }

    [Fact]
    public async Task Adjunta_clase_de_rol_desde_la_definicion()
    {
        var handler = new CannedHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/roleDefinitions"))
                return Json("""
                    {"value":[
                      {"id":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/owner",
                       "properties":{"roleName":"Owner","type":"BuiltInRole",
                        "permissions":[{"actions":["*"],"notActions":[]}]}},
                      {"id":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/reader",
                       "properties":{"roleName":"Reader","type":"BuiltInRole",
                        "permissions":[{"actions":["*/read"],"notActions":[]}]}},
                      {"id":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/custom",
                       "properties":{"roleName":"Soporte","type":"CustomRole",
                        "permissions":[{"actions":["Microsoft.Compute/virtualMachines/*"],"notActions":[]}]}}]}
                    """);
            if (url.Contains("/roleAssignments"))
                return Json("""
                    {"value":[
                      {"properties":{"principalId":"u1","principalType":"User",
                        "roleDefinitionId":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/owner","scope":"/subscriptions/s1"}},
                      {"properties":{"principalId":"u2","principalType":"User",
                        "roleDefinitionId":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/reader","scope":"/subscriptions/s1"}},
                      {"properties":{"principalId":"u3","principalType":"User",
                        "roleDefinitionId":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/custom","scope":"/subscriptions/s1"}}
                    ]}
                    """);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var rows = await Client(handler).GetRoleAssignmentsAsync(1, "s1");

        Assert.Equal("owner", rows[0].RoleClass);
        Assert.False(rows[0].IsCustomRole);
        Assert.Equal("lectura", rows[1].RoleClass);
        Assert.Equal("escritura_servicio", rows[2].RoleClass);
        Assert.True(rows[2].IsCustomRole);
    }

    [Fact]
    public async Task Definicion_ausente_deja_clase_nula()
    {
        // Rol definido en otra rama de management group: no está en las definiciones de la
        // suscripción. "Sin clasificar" es la respuesta honesta; asumir "lectura" sería peor.
        var handler = new CannedHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/roleDefinitions")) return Json("""{"value":[]}""");
            if (url.Contains("/roleAssignments"))
                return Json("""
                    {"value":[{"properties":{"principalId":"u1","principalType":"User",
                        "roleDefinitionId":"/subscriptions/s1/providers/Microsoft.Authorization/roleDefinitions/desconocido","scope":"/subscriptions/s1"}}]}
                    """);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var rows = await Client(handler).GetRoleAssignmentsAsync(1, "s1");

        Assert.Null(rows[0].RoleClass);
        Assert.False(rows[0].IsCustomRole);
    }
}
