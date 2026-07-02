using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.FinOpsData;

public sealed record RiDiagnosticRow(
    int CostResultId, string? ServiceKey, string? ResourceName, string? SkuName, string? Location, string? PaygMeterId);

public interface IRiDiagnosticsQuery
{
    Task<IReadOnlyList<RiDiagnosticRow>> ListAsync(int analysisId, CancellationToken ct);
}

/// <summary>Filas elegibles a RI SIN precio RI hallado = posibles huecos del selector de precios.</summary>
public sealed class SqlRiDiagnosticsQuery(ISqlConnectionFactory factory) : IRiDiagnosticsQuery
{
    public async Task<IReadOnlyList<RiDiagnosticRow>> ListAsync(int analysisId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT cr.cost_result_id, cr.service_key, r.resource_name, r.sku_name, r.location, cr.payg_meter_id
            FROM dbo.cost_results cr
            INNER JOIN dbo.azure_resources r ON r.resource_id = cr.resource_id
            WHERE cr.analysis_id = @a
              AND cr.ri_eligibility = 'eligible'
              AND cr.ri_1y_monthly IS NULL
              AND cr.calculation_status = 'calculated'
            ORDER BY cr.service_key, r.resource_name
            """;
        cmd.Parameters.Add(new SqlParameter("@a", analysisId));
        var list = new List<RiDiagnosticRow>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new RiDiagnosticRow(
                rd.GetInt32(0),
                rd.IsDBNull(1) ? null : rd.GetString(1),
                rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetString(5)));
        return list;
    }
}
