using Microsoft.Extensions.Caching.Memory;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Auth;

/// <summary>
/// Decisión de acceso por módulo con caché en memoria (TTL 60 s por rol).
/// Reglas duras: admin pasa siempre; lector jamás edita (aunque la BD diga lo
/// contrario); fila ausente = denegado.
/// </summary>
public interface IModulePermissionService
{
    Task<bool> HasAccessAsync(string role, string moduleKey, bool requireEdit, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, ModulePermission>> GetForRoleAsync(string role, CancellationToken ct = default);
    void Invalidate();
}

public sealed class ModulePermissionService(IModulePermissionStore store, IMemoryCache cache) : IModulePermissionService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private static string CacheKey(string role) => $"module-perms:{role.Trim().ToLowerInvariant()}";

    public async Task<bool> HasAccessAsync(string role, string moduleKey, bool requireEdit, CancellationToken ct = default)
    {
        if (string.Equals(role, Roles.Admin, StringComparison.OrdinalIgnoreCase)) return true;
        if (requireEdit && string.Equals(role, Roles.Lector, StringComparison.OrdinalIgnoreCase)) return false;

        var perms = await GetForRoleAsync(role, ct);
        return perms.TryGetValue(moduleKey, out var p) && (requireEdit ? p.CanEdit : p.CanView);
    }

    public async Task<IReadOnlyDictionary<string, ModulePermission>> GetForRoleAsync(string role, CancellationToken ct = default)
    {
        var key = CacheKey(role);
        if (cache.TryGetValue(key, out IReadOnlyDictionary<string, ModulePermission>? cached) && cached is not null)
            return cached;
        var fresh = await store.GetForRoleAsync(role, ct);
        cache.Set(key, fresh, Ttl);
        return fresh;
    }

    public void Invalidate()
    {
        cache.Remove(CacheKey(Roles.Consultor));
        cache.Remove(CacheKey(Roles.Lector));
    }
}
