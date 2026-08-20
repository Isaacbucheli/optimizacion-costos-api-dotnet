using System.Security.Claims;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;

namespace OptimizacionCostos.Api.Tests.Auth;

public sealed class GlobalAccessRolesTests
{
    private static ClaimsPrincipal PrincipalWithRole(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));

    [Theory]
    [InlineData(Roles.Admin, true)]
    [InlineData(Roles.Monitoreo, true)]
    [InlineData(Roles.Consultor, false)]
    [InlineData(Roles.Lector, false)]
    public void Acceso_global_a_clientes_solo_admin_y_monitoreo(string role, bool expected) =>
        Assert.Equal(expected, SqlAnalysisAccess.HasGlobalAccess(PrincipalWithRole(role)));
}
