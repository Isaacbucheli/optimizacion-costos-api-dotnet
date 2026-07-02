using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.FinOpsData;

/// <summary>Lookups sobre las tablas finops_* con caché in-memory (TTL 1h).</summary>
public sealed class SqlFinOpsRefData(ISqlConnectionFactory factory, IMemoryCache cache) : IFinOpsRefData
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    public async Task<IReadOnlyDictionary<string, bool>> GetRiEligibilityAsync(
        IReadOnlyCollection<string> meterIds, CancellationToken ct = default)
    {
        if (meterIds.Count == 0) return new Dictionary<string, bool>();
        // La tabla completa (~121k) cabe en memoria (~8 MB) y evita armar IN(...) gigantes.
        var all = await cache.GetOrCreateAsync("finops:eligibility", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = Ttl;
            return await LoadEligibilityAsync(ct);
        });
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in meterIds)
            if (all!.TryGetValue(m, out var ri)) result[m] = ri;
        return result;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetRegionNamesAsync(CancellationToken ct = default)
        => (await cache.GetOrCreateAsync("finops:regions", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = Ttl;
            return await LoadRegionsAsync(ct);
        }))!;

    public async Task<IReadOnlyDictionary<string, FinOpsResourceTypeInfo>> GetResourceTypesAsync(CancellationToken ct = default)
        => (await cache.GetOrCreateAsync("finops:resource_types", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = Ttl;
            return await LoadResourceTypesAsync(ct);
        }))!;

    private async Task<Dictionary<string, bool>> LoadEligibilityAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await factory.OpenAsync(ct);
        await SqlFinOpsDataStore.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT meter_id, ri_eligible FROM dbo.finops_commitment_eligibility";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[r.GetString(0)] = r.GetBoolean(1);
        return map;
    }

    private async Task<Dictionary<string, string>> LoadRegionsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await factory.OpenAsync(ct);
        await SqlFinOpsDataStore.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT region_id, MAX(region_name) FROM dbo.finops_regions
            WHERE region_id IS NOT NULL AND region_name IS NOT NULL GROUP BY region_id
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    private async Task<Dictionary<string, FinOpsResourceTypeInfo>> LoadResourceTypesAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, FinOpsResourceTypeInfo>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await factory.OpenAsync(ct);
        await SqlFinOpsDataStore.EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT rt.resource_type, rt.singular_display_name,
                   (SELECT MAX(s.service_category) FROM dbo.finops_services s
                     WHERE s.resource_type = rt.resource_type) AS service_category
            FROM dbo.finops_resource_types rt
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[r.GetString(0)] = new FinOpsResourceTypeInfo(
                r.IsDBNull(1) ? r.GetString(0) : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2));
        return map;
    }
}
