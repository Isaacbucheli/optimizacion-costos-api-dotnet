using Microsoft.Extensions.Caching.Memory;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Auth;

/// <summary>
/// Decisión de acceso por módulo con caché en memoria (TTL 60 s por rol).
/// Reglas duras: admin pasa siempre; lector y monitoreo jamás editan (aunque la BD
/// diga lo contrario); fila ausente = denegado.
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
        if (requireEdit && Roles.IsReadOnly(role)) return false;

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

    // Solo limpia la caché en memoria de ESTA instancia del proceso. Con varias instancias
    // de App Service, las demás no se enteran de este PUT y siguen sirviendo su copia
    // cacheada hasta que expire: el límite de seguridad real es el TTL de 60s de arriba,
    // no esta invalidación (que es solo una optimización de latencia para la instancia local).
    public void Invalidate()
    {
        cache.Remove(CacheKey(Roles.Consultor));
        cache.Remove(CacheKey(Roles.Lector));
        cache.Remove(CacheKey(Roles.Monitoreo));
    }
}
