using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Data;

/// <summary>Abre conexiones a Azure SQL. Equivale a database.get_connection() del FastAPI.</summary>
public interface ISqlConnectionFactory
{
    Task<SqlConnection> OpenAsync(CancellationToken ct = default);
}
