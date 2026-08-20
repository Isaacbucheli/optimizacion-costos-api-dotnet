using Microsoft.Extensions.Caching.Memory;
using OptimizacionCostos.Api.Auth;

namespace OptimizacionCostos.Api.Tests.Auth;

public sealed class MonitoreoRoleTests
{
    [Fact]
    public void Monitoreo_es_rol_valido_y_de_solo_lectura()
    {
        Assert.Contains(Roles.Monitoreo, Roles.Valid);
        Assert.True(Roles.IsReadOnly(Roles.Monitoreo));
        Assert.True(Roles.IsReadOnly(Roles.Lector));
        Assert.False(Roles.IsReadOnly(Roles.Consultor));
        Assert.False(Roles.IsReadOnly(Roles.Admin));
        // Editors es el CSV de [Authorize]: monitoreo NO muta.
        Assert.DoesNotContain("monitoreo", Roles.Editors);
    }

    private static ModulePermissionService NewService(FakeModulePermissionStore store) =>
        new(store, new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task Monitoreo_ve_solo_con_fila_y_jamas_edita()
    {
        // can_edit sucio a propósito: el candado debe ignorarlo.
        var store = new FakeModulePermissionStore()
            .Set(Roles.Monitoreo, Modules.Alerts, canView: true, canEdit: true);
        var svc = NewService(store);

        Assert.True(await svc.HasAccessAsync(Roles.Monitoreo, Modules.Alerts, requireEdit: false));
        Assert.False(await svc.HasAccessAsync(Roles.Monitoreo, Modules.Alerts, requireEdit: true));
        Assert.False(await svc.HasAccessAsync(Roles.Monitoreo, Modules.Policies, requireEdit: false));
    }

    [Fact]
    public async Task Invalidate_limpia_la_cache_de_monitoreo()
    {
        var store = new FakeModulePermissionStore()
            .Set(Roles.Monitoreo, Modules.Alerts, canView: false, canEdit: false);
        var svc = NewService(store);
        Assert.False(await svc.HasAccessAsync(Roles.Monitoreo, Modules.Alerts, requireEdit: false));

        store.Set(Roles.Monitoreo, Modules.Alerts, canView: true, canEdit: false);
        svc.Invalidate();
        Assert.True(await svc.HasAccessAsync(Roles.Monitoreo, Modules.Alerts, requireEdit: false));
    }
}
