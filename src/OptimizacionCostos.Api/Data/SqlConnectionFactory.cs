using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Data;

/// <summary>
/// Implementacion con Microsoft.Data.SqlClient (TDS nativo, sin ODBC).
/// Mismo servidor/BD/credenciales y Encrypt=yes que el FastAPI.
///
/// La apertura va con reintento por falla transitoria (<see cref="SqlTransientRetry"/>): sin el, un
/// corte de red del lado de Azure le devuelve 500 a quien haya pedido algo en ese momento.
/// </summary>
public sealed class SqlConnectionFactory(AppConfig config, ILogger<SqlConnectionFactory> logger)
    : ISqlConnectionFactory
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

    public Task<SqlConnection> OpenAsync(CancellationToken ct = default) =>
        SqlTransientRetry.EjecutarAsync(
            AbrirAsync,
            ex => ex is SqlException sql && SqlTransientRetry.EsReintentable(sql.Number),
            async (intento, causa, espera, token) =>
            {
                // Aviso y no error: la peticion todavia puede terminar bien. Igual queda registrado,
                // porque un reintento que se vuelve frecuente es la senal de que algo mas pasa en la
                // base y sin esta linea el problema se volveria invisible al arreglar el sintoma.
                logger.LogWarning(
                    "Fallo transitorio al abrir la conexion a SQL (intento {Intento} de {Total}); " +
                    "se reintenta en {Espera} ms. Causa: {Causa}",
                    intento, SqlTransientRetry.Intentos, espera.TotalMilliseconds, causa.Message);
                await Task.Delay(espera, token);
            },
            ct);

    private async Task<SqlConnection> AbrirAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            // Sin esto, cada intento fallido deja un SqlConnection sin liberar: el pool lo cuenta y
            // una racha de fallos terminaria agotandolo, convirtiendo un corte pasajero en uno largo.
            await conn.DisposeAsync();
            throw;
        }
    }
}
