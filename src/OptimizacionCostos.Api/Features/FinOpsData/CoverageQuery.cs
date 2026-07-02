using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.FinOpsData;

public sealed record CoverageGap(string ResourceType, string? DisplayName, string? ServiceCategory, int Count);
public sealed record CoverageResult(int TotalResources, int CostedResources, double CoveragePct, IReadOnlyList<CoverageGap> Uncovered);

public interface ICoverageQuery
{
    Task<CoverageResult> GetAsync(int analysisId, CancellationToken ct);
}

/// <summary>% del inventario con cost_result + tipos de recurso sin calculadora (base para decidir calculadoras futuras).</summary>
public sealed class SqlCoverageQuery(ISqlConnectionFactory factory, IFinOpsRefData refData) : ICoverageQuery
{
    public async Task<CoverageResult> GetAsync(int analysisId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT LOWER(r.resource_type) AS rt,
                   COUNT(*) AS total,
                   SUM(CASE WHEN cr.cost_result_id IS NULL THEN 0 ELSE 1 END) AS costed
            FROM dbo.azure_resources r
            LEFT JOIN dbo.cost_results cr
                ON cr.resource_id = r.resource_id AND cr.analysis_id = r.analysis_id
            WHERE r.analysis_id = @a AND r.is_active = 1
            GROUP BY LOWER(r.resource_type)
            """;
        cmd.Parameters.Add(new SqlParameter("@a", analysisId));

        var total = 0; var costed = 0;
        var gaps = new List<(string Rt, int Count)>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var rt = rd.IsDBNull(0) ? "(desconocido)" : rd.GetString(0);
            var t = rd.GetInt32(1);
            var c = rd.GetInt32(2);
            total += t; costed += c;
            if (c == 0) gaps.Add((rt, t));
        }

        var types = await refData.GetResourceTypesAsync(ct);
        var uncovered = gaps
            .OrderByDescending(g => g.Count)
            .Select(g => new CoverageGap(
                g.Rt,
                types.TryGetValue(g.Rt, out var info) ? info.DisplayName : null,
                types.TryGetValue(g.Rt, out var i2) ? i2.ServiceCategory : null,
                g.Count))
            .ToList();

        var pct = total == 0 ? 0.0 : Math.Round(costed * 100.0 / total, 1);
        return new CoverageResult(total, costed, pct, uncovered);
    }
}
