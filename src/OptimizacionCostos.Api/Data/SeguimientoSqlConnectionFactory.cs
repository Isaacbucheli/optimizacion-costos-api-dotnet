using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Data;

/// <summary>
/// Implementación con Microsoft.Data.SqlClient, mismos parámetros de seguridad que
/// <see cref="SqlConnectionFactory"/> (Encrypt sí, TrustServerCertificate no).
///
/// Si la config está incompleta NO lanza en el constructor: se registra como singleton al arrancar y
/// una variable faltante no debe tumbar la API ni afectar a los otros módulos. El controlador
/// consulta <see cref="IsConfigured"/> y responde 503.
/// </summary>
public sealed class SeguimientoSqlConnectionFactory : ISeguimientoSqlConnectionFactory
{
    private readonly string? _connectionString;

    public SeguimientoSqlConnectionFactory(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Sql2Server) ||
            string.IsNullOrWhiteSpace(config.Sql2Database) ||
            string.IsNullOrWhiteSpace(config.Sql2Username) ||
            string.IsNullOrWhiteSpace(config.Sql2Password))
        {
            _connectionString = null;
            return;
        }

        _connectionString = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{config.Sql2Server},1433",
            InitialCatalog = config.Sql2Database,
            UserID = config.Sql2Username,
            Password = config.Sql2Password,
            Encrypt = true,
            TrustServerCertificate = false,
            ConnectTimeout = 30,
        }.ConnectionString;
    }

    public bool IsConfigured => _connectionString is not null;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        if (_connectionString is null)
            throw new InvalidOperationException("La BD del tablero de pendientes no está configurada.");

        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
