using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Data;

/// <summary>
/// Implementacion con Microsoft.Data.SqlClient (TDS nativo, sin ODBC).
/// Mismo servidor/BD/credenciales y Encrypt=yes que el FastAPI.
/// </summary>
public sealed class SqlConnectionFactory(AppConfig config) : ISqlConnectionFactory
{
    private readonly string _connectionString = new SqlConnectionStringBuilder
    {
        DataSource = $"tcp:{config.SqlServer},1433",
        InitialCatalog = config.SqlDatabase,
        UserID = config.SqlUsername,
        Password = config.SqlPassword,
        Encrypt = true,
        TrustServerCertificate = false,
        ConnectTimeout = 30,
    }.ConnectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
