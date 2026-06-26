using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.CostEngine.Engine;

/// <summary>
/// Carga recursos del inventario para costear. Port de _load_resources_for_service y
/// los enriquecimientos de cost_engine.py. SQL parametrizado; el nombre de la tabla
/// detalle proviene del catálogo (no del usuario) y se valida contra una whitelist de patrón.
/// </summary>
public sealed partial class SqlResourceLoader(ISqlConnectionFactory factory) : IResourceLoader
{
    private const string BaseSelect = """
        r.resource_id, r.azure_resource_id, r.subscription_id, r.subscription_name,
        r.resource_group, r.resource_name, r.resource_type, r.service_category,
        r.location, r.region_name, r.sku_name, r.sku_tier, r.size_name,
        r.status, r.os_type, r.license_type, r.properties_json
        """;

    public IReadOnlyList<ResourceRow> LoadResourcesForService(
        int analysisId, ServiceCatalogEntry service, int resourceOffset = 0, int? resourceLimit = null)
    {
        var detailTable = service.DetailTableName;

        string paging;
        if (resourceLimit is not null)
        {
            paging = " ORDER BY r.resource_id OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY";
        }
        else
        {
            paging = " ORDER BY r.resource_id";
        }

        string sql;
        if (string.IsNullOrEmpty(detailTable))
        {
            sql = $"""
                SELECT {BaseSelect}
                FROM dbo.azure_resources r
                WHERE r.analysis_id = @analysisId AND r.resource_type = @rtype AND r.is_active = 1
                {paging}
                """;
        }
        else
        {
            if (!SafeTableName().IsMatch(detailTable))
            {
                throw new InvalidOperationException($"Nombre de tabla detalle inválido: {detailTable}");
            }
            sql = $"""
                SELECT {BaseSelect}, d.*
                FROM dbo.azure_resources r
                INNER JOIN dbo.{detailTable} d ON d.resource_id = r.resource_id
                WHERE r.analysis_id = @analysisId AND r.resource_type = @rtype AND r.is_active = 1
                {paging}
                """;
        }

        using var conn = factory.OpenAsync().GetAwaiter().GetResult();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@analysisId", analysisId));
        cmd.Parameters.Add(new SqlParameter("@rtype", service.AzureResourceType));
        if (resourceLimit is not null)
        {
            cmd.Parameters.Add(new SqlParameter("@offset", Math.Max(0, resourceOffset)));
            cmd.Parameters.Add(new SqlParameter("@limit", Math.Max(1, resourceLimit.Value)));
        }

        var rows = new List<ResourceRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // dict(zip(columns, row)): si hay columnas con igual nombre (r.resource_id y
            // d.resource_id), gana la ÚLTIMA, igual que en Python.
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                dict[reader.GetName(i)] = value;
            }
            rows.Add(new ResourceRow(dict));
        }
        return rows;
    }

    public IReadOnlyDictionary<string, SqlVmInfo> LoadSqlVmInfo(int analysisId)
    {
        using var conn = factory.OpenAsync().GetAwaiter().GetResult();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.vm_azure_resource_id, s.sql_image_sku, s.sql_license_type, s.sql_image_offer
            FROM dbo.sql_vm_details s
            INNER JOIN dbo.azure_resources r ON r.resource_id = s.resource_id
            WHERE r.analysis_id = @analysisId
            """;
        cmd.Parameters.Add(new SqlParameter("@analysisId", analysisId));

        var map = new Dictionary<string, SqlVmInfo>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var vmId = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (string.IsNullOrEmpty(vmId)) continue;
            map[vmId.ToLowerInvariant()] = new SqlVmInfo(
                SqlImageSku: reader.IsDBNull(1) ? null : reader.GetString(1),
                SqlLicenseType: reader.IsDBNull(2) ? null : reader.GetString(2),
                SqlImageOffer: reader.IsDBNull(3) ? null : reader.GetString(3));
        }
        return map;
    }

    public IReadOnlySet<string> LoadStoppedVmNames(int analysisId)
    {
        using var conn = factory.OpenAsync().GetAwaiter().GetResult();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.resource_name, v.power_state
            FROM dbo.azure_resources r
            INNER JOIN dbo.vm_details v ON v.resource_id = r.resource_id
            WHERE r.analysis_id = @analysisId AND r.resource_type = 'microsoft.compute/virtualmachines'
            """;
        cmd.Parameters.Add(new SqlParameter("@analysisId", analysisId));

        var stopped = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(0) ? null : reader.GetString(0);
            var power = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (!string.IsNullOrEmpty(power) && !power.ToLowerInvariant().Contains("running"))
            {
                stopped.Add((name ?? "").ToLowerInvariant());
            }
        }
        return stopped;
    }

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex SafeTableName();
}
