using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.CostEngine.Engine;

/// <summary>
/// Lee dbo.service_catalog. Equivale a service_catalog.list_services.
/// NOTA: asume que el catálogo ya está sembrado (el app FastAPI lo crea con
/// ensure_catalog_defaults); aquí solo se LEE (no se escribe sobre prod).
/// </summary>
public sealed class SqlServiceCatalog(ISqlConnectionFactory factory) : IServiceCatalog
{
    public IReadOnlyList<ServiceCatalogEntry> ListServices(bool onlyActive = false)
    {
        using var conn = factory.OpenAsync().GetAwaiter().GetResult();
        using var cmd = conn.CreateCommand();
        var where = onlyActive ? "WHERE is_active = 1" : "";
        cmd.CommandText = $"""
            SELECT service_key, display_name, azure_resource_type, service_category,
                   detail_table_name, inserter_key, calculator_key,
                   display_order, is_active, kql_query
            FROM dbo.service_catalog
            {where}
            ORDER BY display_order, service_key
            """;

        var list = new List<ServiceCatalogEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ServiceCatalogEntry
            {
                ServiceKey = reader.GetString(0),
                DisplayName = reader.IsDBNull(1) ? null : reader.GetString(1),
                AzureResourceType = reader.GetString(2),
                ServiceCategory = reader.IsDBNull(3) ? null : reader.GetString(3),
                DetailTableName = reader.IsDBNull(4) ? null : reader.GetString(4),
                InserterKey = reader.IsDBNull(5) ? null : reader.GetString(5),
                CalculatorKey = reader.GetString(6),
                DisplayOrder = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                IsActive = reader.GetBoolean(8),
                KqlQuery = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }
        return list;
    }
}
