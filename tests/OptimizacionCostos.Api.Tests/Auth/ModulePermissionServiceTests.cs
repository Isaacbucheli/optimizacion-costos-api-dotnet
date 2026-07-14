using Microsoft.Extensions.Caching.Memory;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests.Auth;

public sealed class ModulePermissionServiceTests
{
    /// <summary>Store contable para verificar caché (no usa el de Fakes.cs para poder contar llamadas).</summary>
    private sealed class CountingStore : IModulePermissionStore
    {
        public int Calls;
        public Dictionary<string, ModulePermission> RolePerms = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, ModulePermission>> GetForRoleAsync(string role, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<string, ModulePermission>>(RolePerms);
        }
        public Task<IReadOnlyDictionary<string, IReadOnlyList<ModulePermission>>> GetMatrixAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ModulePermission>>>(
                new Dictionary<string, IReadOnlyList<ModulePermission>>());
        public Task ReplaceMatrixAsync(IReadOnlyDictionary<string, IReadOnlyList<ModulePermission>> matrix, string updatedBy, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static ModulePermissionService Build(CountingStore store)
        => new(store, new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task Admin_pasa_siempre_sin_consultar_el_store()
    {
        var store = new CountingStore();
        var svc = Build(store);
        Assert.True(await svc.HasAccessAsync(Roles.Admin, Modules.Alerts, requireEdit: false));
        Assert.True(await svc.HasAccessAsync(Roles.Admin, Modules.Alerts, requireEdit: true));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Lector_nunca_edita_aunque_la_matriz_diga_que_si()
    {
        var store = new CountingStore();
        store.RolePerms[Modules.Alerts] = new ModulePermission(Modules.Alerts, CanView: true, CanEdit: true);
        var svc = Build(store);
        Assert.False(await svc.HasAccessAsync(Roles.Lector, Modules.Alerts, requireEdit: true));
        Assert.True(await svc.HasAccessAsync(Roles.Lector, Modules.Alerts, requireEdit: false));
    }

    [Fact]
    public async Task Fila_ausente_niega_el_acceso()
    {
        var svc = Build(new CountingStore());
        Assert.False(await svc.HasAccessAsync(Roles.Consultor, Modules.Policies, requireEdit: false));
    }

    [Fact]
    public async Task Consultor_segun_matriz_ver_y_editar()
    {
        var store = new CountingStore();
        store.RolePerms[Modules.Alerts] = new ModulePermission(Modules.Alerts, CanView: true, CanEdit: false);
        var svc = Build(store);
        Assert.True(await svc.HasAccessAsync(Roles.Consultor, Modules.Alerts, requireEdit: false));
        Assert.False(await svc.HasAccessAsync(Roles.Consultor, Modules.Alerts, requireEdit: true));
    }

    [Fact]
    public async Task Cachea_por_rol_e_invalidate_fuerza_relectura()
    {
        var store = new CountingStore();
        store.RolePerms[Modules.Alerts] = new ModulePermission(Modules.Alerts, true, true);
        var svc = Build(store);

        await svc.HasAccessAsync(Roles.Consultor, Modules.Alerts, false);
        await svc.HasAccessAsync(Roles.Consultor, Modules.Alerts, true);
        Assert.Equal(1, store.Calls); // segunda llamada sale del caché

        svc.Invalidate();
        await svc.HasAccessAsync(Roles.Consultor, Modules.Alerts, false);
        Assert.Equal(2, store.Calls); // tras invalidar, relee
    }
}
