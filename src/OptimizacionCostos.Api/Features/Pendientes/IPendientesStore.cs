namespace OptimizacionCostos.Api.Features.Pendientes;

/// <summary>
/// Acceso a la BD del tablero de pendientes/bloqueantes. Todas las operaciones reciben el área porque
/// es parte de la clave del dato (<c>dbo.Cliente</c> tiene PK (Area, Num)) y porque cada área es un
/// módulo de permisos distinto.
///
/// La SWA del tablero escribe la misma base en paralelo, así que las escrituras son granulares y la
/// edición de un pendiente va con concurrencia optimista por <c>Actualizado</c>.
/// </summary>
public interface IPendientesStore
{
    Task<PendientesPayload> GetAreaAsync(string area, CancellationToken ct = default);

    Task<PendienteItem?> GetItemAsync(string area, string id, CancellationToken ct = default);

    /// <summary>Alta. Devuelve el Id generado por el backend (prefijo propio, ver SqlPendientesStore).</summary>
    Task<string> CreateItemAsync(string area, PendienteWrite data, CancellationToken ct = default);

    /// <summary>Edición. Conflict si el <c>Actualizado</c> enviado ya no es el de la fila.</summary>
    Task<WriteOutcome> UpdateItemAsync(string area, string id, PendienteWrite data, CancellationToken ct = default);

    /// <summary>Borra el pendiente y sus notas en una transacción.</summary>
    Task<bool> DeleteItemAsync(string area, string id, CancellationToken ct = default);

    /// <summary>Agrega una nota al final del timeline. Null si el pendiente no existe en el área.</summary>
    Task<int?> AddNotaAsync(string area, string id, NotaWrite data, string? autor, CancellationToken ct = default);

    /// <summary>Borra una nota por HistId, validando que pertenezca a ese pendiente y área.</summary>
    Task<bool> DeleteNotaAsync(string area, string id, int histId, CancellationToken ct = default);

    Task<bool> ClienteExistsAsync(string area, int num, CancellationToken ct = default);

    /// <summary>Alta de cliente. Devuelve el <c>Num</c> asignado (MAX+1 del área).</summary>
    Task<int> CreateClienteAsync(string area, ClienteWrite data, CancellationToken ct = default);

    Task<bool> UpdateClienteAsync(string area, int num, ClienteWrite data, CancellationToken ct = default);

    /// <summary>Borra un cliente. Se niega si tiene pendientes: nunca cascada.</summary>
    Task<ClienteDeleteOutcome> DeleteClienteAsync(string area, int num, CancellationToken ct = default);
}
