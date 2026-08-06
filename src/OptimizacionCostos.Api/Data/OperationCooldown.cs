using Microsoft.Data.SqlClient;

namespace OptimizacionCostos.Api.Data;

/// <summary>
/// Calculo del enfriamiento, separado del SQL a proposito: la consulta necesita base de datos y la
/// suite de CI corre sin una, asi que los bordes (enfriamiento apagado, sin corrida previa, reloj
/// hacia atras) se prueban aca sin SQL. La consulta solo aporta los segundos transcurridos.
/// </summary>
public static class CooldownWindow
{
    /// <summary>
    /// Cuanto falta para poder volver a ejecutar, o <c>null</c> si ya se puede.
    /// </summary>
    /// <param name="elapsedSeconds">
    /// Segundos desde la ultima ejecucion, o <c>null</c> si no hay ninguna registrada.
    /// </param>
    public static TimeSpan? Remaining(int? elapsedSeconds, TimeSpan cooldown)
    {
        if (cooldown <= TimeSpan.Zero) return null; // enfriamiento desactivado
        if (elapsedSeconds is null) return null;    // primera ejecucion

        // Se acota a >= 0: un salto de reloj hacia atras daria un transcurrido negativo y volveria el
        // enfriamiento mas largo que lo configurado, o incluso eterno.
        var transcurrido = TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds.Value));
        return transcurrido < cooldown ? cooldown - transcurrido : null;
    }
}

/// <summary>
/// Enfriamiento por operacion, para los endpoints que disparan trabajo real contra Azure. El limite de
/// tasa global frena la rafaga; esto expresa la regla de negocio: la informacion de origen no cambia
/// en segundos, asi que repetir la operacion solo gasta llamadas contra la suscripcion del cliente.
///
/// Se aplica SOLO donde no existe una re-ejecucion legitima e inmediata. No va, por ejemplo, en el
/// sync de suscripciones (el consultor agrega una credencial, sincroniza, agrega otra y vuelve a
/// sincronizar) ni en el refresco del score de Advisor (el flujo normal es consultar y luego
/// refrescar). Un enfriamiento ahi romperia trabajo real, que es peor que la llamada de mas.
/// </summary>
public interface IOperationCooldown
{
    /// <summary>
    /// Reserva la ejecucion. Devuelve <c>null</c> si se puede ejecutar (y deja registrado el momento),
    /// o cuanto falta si todavia esta en enfriamiento (y entonces NO registra nada).
    /// </summary>
    /// <param name="operationKey">Identificador estable de la operacion, ej. "optimization-scan".</param>
    /// <param name="clientId">Cliente afectado, o <c>null</c> para una operacion global.</param>
    Task<TimeSpan?> TryBeginAsync(string operationKey, int? clientId, TimeSpan cooldown, CancellationToken ct);
}

public sealed class SqlOperationCooldown(ISqlConnectionFactory factory) : IOperationCooldown
{
    /// <summary>Las operaciones globales usan 0 y no NULL, para que la clave primaria sea utilizable.</summary>
    private const int GlobalClientId = 0;

    public async Task<TimeSpan?> TryBeginAsync(
        string operationKey, int? clientId, TimeSpan cooldown, CancellationToken ct)
    {
        if (cooldown <= TimeSpan.Zero) return null; // desactivado: no bloquea ni registra

        await using var conn = await factory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        // Serializable + UPDLOCK/HOLDLOCK: la comprobacion y el registro tienen que ser atomicos, o
        // dos peticiones en paralelo pasan las dos, que es justo el patron que se quiere frenar.
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);

        var cid = clientId ?? GlobalClientId;
        int? transcurrido;
        await using (var leer = conn.CreateCommand())
        {
            leer.Transaction = tx;
            // El transcurrido lo calcula SQL Server y no el proceso, para no depender de que los
            // relojes del App Service y de la base esten sincronizados.
            leer.CommandText = """
                SELECT DATEDIFF(SECOND, last_started_at, SYSUTCDATETIME())
                FROM dbo.operation_cooldown WITH (UPDLOCK, HOLDLOCK)
                WHERE operation_key = @op AND client_id = @cid
                """;
            leer.Parameters.Add(new SqlParameter("@op", operationKey));
            leer.Parameters.Add(new SqlParameter("@cid", cid));
            var raw = await leer.ExecuteScalarAsync(ct);
            transcurrido = raw is null or DBNull ? null : Convert.ToInt32(raw);
        }

        var falta = CooldownWindow.Remaining(transcurrido, cooldown);
        if (falta is not null)
        {
            await tx.CommitAsync(ct);
            return falta;
        }

        await using (var marcar = conn.CreateCommand())
        {
            marcar.Transaction = tx;
            marcar.CommandText = """
                UPDATE dbo.operation_cooldown SET last_started_at = SYSUTCDATETIME()
                WHERE operation_key = @op AND client_id = @cid;
                IF @@ROWCOUNT = 0
                    INSERT INTO dbo.operation_cooldown (operation_key, client_id, last_started_at)
                    VALUES (@op, @cid, SYSUTCDATETIME());
                """;
            marcar.Parameters.Add(new SqlParameter("@op", operationKey));
            marcar.Parameters.Add(new SqlParameter("@cid", cid));
            await marcar.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return null;
    }

    private static async Task EnsureSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.operation_cooldown', 'U') IS NULL
            BEGIN
                CREATE TABLE dbo.operation_cooldown (
                    operation_key NVARCHAR(80) NOT NULL,
                    client_id INT NOT NULL,
                    last_started_at DATETIME2 NOT NULL,
                    CONSTRAINT PK_operation_cooldown PRIMARY KEY (operation_key, client_id)
                );
            END
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
