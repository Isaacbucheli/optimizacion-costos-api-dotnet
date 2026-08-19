using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.AlertCatalog;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests;

/// <summary>Directorio de usuarios en memoria (sin BD). Mapea email -> usuario/rol.</summary>
public sealed class FakeUserDirectory : IUserDirectory
{
    public Dictionary<string, AppUser> Users { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string email, string role, bool active = true, DateTime? revokedAt = null) =>
        Users[email] = new AppUser(email, "Test " + role, role, active, revokedAt);

    public Task<AppUser?> FindActiveByEmailAsync(string email, CancellationToken ct = default)
    {
        if (Users.TryGetValue(email, out var u) && u.IsActive)
            return Task.FromResult<AppUser?>(u);
        return Task.FromResult<AppUser?>(null);
    }
}

/// <summary>Catalogo en memoria (sin BD) para probar el pipeline HTTP completo.</summary>
public sealed class FakeAlertCatalogStore : IAlertCatalogStore
{
    private readonly List<AlertItem> _alerts = [];
    private readonly List<KqlItem> _kql = [];
    private int _alertSeq;
    private int _kqlSeq;

    public FakeAlertCatalogStore Seed()
    {
        _alerts.Add(new AlertItem { AlertId = ++_alertSeq, AlertNumber = 1, Name = "Alerta seed", IsActive = true });
        _kql.Add(new KqlItem { KqlId = ++_kqlSeq, Name = "KQL seed", IsActive = true });
        return this;
    }

    public Task<IReadOnlyList<AlertItem>> ListAlertsAsync(bool includeInactive = false, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlertItem>>(_alerts.Where(a => includeInactive || a.IsActive).ToList());

    public Task<AlertItem?> GetAlertAsync(int alertId, CancellationToken ct = default)
        => Task.FromResult(_alerts.FirstOrDefault(a => a.AlertId == alertId));

    public Task<int> CreateAlertAsync(AlertCreate data, CancellationToken ct = default)
    {
        var item = new AlertItem { AlertId = ++_alertSeq, Name = data.Name, AlertNumber = data.AlertNumber, IsActive = true };
        _alerts.Add(item);
        return Task.FromResult(item.AlertId);
    }

    public Task<bool> UpdateAlertAsync(int alertId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
        => Task.FromResult(_alerts.Any(a => a.AlertId == alertId));

    public Task<bool> SoftDeleteAlertAsync(int alertId, CancellationToken ct = default)
    {
        var idx = _alerts.FindIndex(a => a.AlertId == alertId);
        if (idx < 0) return Task.FromResult(false);
        _alerts[idx] = _alerts[idx] with { IsActive = false };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<KqlItem>> ListKqlAsync(bool includeInactive = false, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<KqlItem>>(_kql.Where(k => includeInactive || k.IsActive).ToList());

    public Task<KqlItem?> GetKqlAsync(int kqlId, CancellationToken ct = default)
        => Task.FromResult(_kql.FirstOrDefault(k => k.KqlId == kqlId));

    public Task<int> CreateKqlAsync(KqlCreate data, CancellationToken ct = default)
    {
        var item = new KqlItem { KqlId = ++_kqlSeq, Name = data.Name, IsActive = true };
        _kql.Add(item);
        return Task.FromResult(item.KqlId);
    }

    public Task<bool> UpdateKqlAsync(int kqlId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
        => Task.FromResult(_kql.Any(k => k.KqlId == kqlId));

    public Task<bool> SoftDeleteKqlAsync(int kqlId, CancellationToken ct = default)
        => Task.FromResult(_kql.RemoveAll(k => k.KqlId == kqlId) > 0);
}

/// <summary>
/// Matriz rol×módulo en memoria. SeedDefaults() replica el seed de producción
/// (consultor todo; lector solo ver, optimization excluido) para que los tests
/// existentes conserven su comportamiento.
/// </summary>
public sealed class FakeModulePermissionStore : IModulePermissionStore
{
    private readonly Dictionary<string, Dictionary<string, ModulePermission>> _matrix = new(StringComparer.OrdinalIgnoreCase);
    public string? LastUpdatedBy { get; private set; }

    public FakeModulePermissionStore SeedDefaults()
    {
        foreach (var m in Modules.All)
        {
            Set(Roles.Consultor, m.Key, canView: true, canEdit: true);
            Set(Roles.Lector, m.Key, canView: m.Key != Modules.Optimization, canEdit: false);
        }
        return this;
    }

    public FakeModulePermissionStore Set(string role, string moduleKey, bool canView, bool canEdit)
    {
        if (!_matrix.TryGetValue(role, out var perms))
            _matrix[role] = perms = new Dictionary<string, ModulePermission>(StringComparer.OrdinalIgnoreCase);
        perms[moduleKey] = new ModulePermission(moduleKey, canView, canEdit);
        return this;
    }

    public Task<IReadOnlyDictionary<string, ModulePermission>> GetForRoleAsync(string role, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, ModulePermission>>(
            _matrix.TryGetValue(role, out var perms) ? perms : new Dictionary<string, ModulePermission>());

    public Task<IReadOnlyDictionary<string, IReadOnlyList<ModulePermission>>> GetMatrixAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ModulePermission>>>(
            _matrix.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ModulePermission>)kv.Value.Values.ToList(), StringComparer.OrdinalIgnoreCase));

    public Task ReplaceMatrixAsync(IReadOnlyDictionary<string, IReadOnlyList<ModulePermission>> matrix, string updatedBy, CancellationToken ct = default)
    {
        LastUpdatedBy = updatedBy;
        foreach (var (role, rows) in matrix)
        {
            _matrix[role] = rows.ToDictionary(r => r.ModuleKey, r => r, StringComparer.OrdinalIgnoreCase);
        }
        return Task.CompletedTask;
    }
}
