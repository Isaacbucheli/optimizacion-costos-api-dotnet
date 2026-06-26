using OptimizacionCostos.Api.Features.CostEngine.Engine;
using Xunit;

namespace OptimizacionCostos.Api.Tests.CostEngine;

/// <summary>Tests de expand_service_keys / INTERNAL_SERVICE_KEYS (service_catalog.py).</summary>
public sealed class ServiceDependenciesTests
{
    [Fact]
    public void Expand_Null_ReturnsNull()
        => Assert.Null(ServiceDependencies.ExpandServiceKeys(null));

    [Fact]
    public void Expand_Empty_ReturnsEmpty()
        => Assert.Empty(ServiceDependencies.ExpandServiceKeys([])!);

    [Fact]
    public void Expand_Vms_AddsSqlVmDependency()
    {
        var r = ServiceDependencies.ExpandServiceKeys(["vms"]);
        Assert.Equal(new[] { "vms", "sql_vm" }, r);
    }

    [Fact]
    public void Expand_NoDependency_Unchanged()
    {
        var r = ServiceDependencies.ExpandServiceKeys(["disks", "sql"]);
        Assert.Equal(new[] { "disks", "sql" }, r);
    }

    [Fact]
    public void Expand_Dedupes()
    {
        var r = ServiceDependencies.ExpandServiceKeys(["vms", "sql_vm"]);
        Assert.Equal(new[] { "vms", "sql_vm" }, r);
    }

    [Fact]
    public void SqlVm_IsInternal()
    {
        Assert.Contains("sql_vm", ServiceDependencies.InternalServiceKeys);
        Assert.DoesNotContain("vms", ServiceDependencies.InternalServiceKeys);
    }
}
