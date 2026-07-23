using OptimizacionCostos.Api.Features.Cdc.AccessReview;

namespace OptimizacionCostos.Api.Tests.Cdc.AccessReview;

public class AccessReviewScopeTests
{
    [Theory]
    [InlineData("/", "root")]
    [InlineData("/providers/Microsoft.Management/managementGroups/mg1", "management_group")]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001", "subscription")]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-app", "resource_group")]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-app/providers/Microsoft.Compute/virtualMachines/vm1", "resource")]
    public void Clasifica_niveles_de_scope(string scope, string expected) =>
        Assert.Equal(expected, AccessReviewScope.Level(scope));
}
