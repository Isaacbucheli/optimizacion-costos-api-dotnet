using Microsoft.Data.SqlClient;
using OptimizacionCostos.Api.Data;

namespace OptimizacionCostos.Api.Features.Cdc;

public sealed record CredentialRef(int CredentialId, string? CredentialName);

/// <summary>
/// Agrega las reservas de todas las credenciales activas de un cliente (dedup por
/// reservation_id, orden por días restantes). Port de list_client_reservations.
/// </summary>
public interface IReservationService
{
    Task<IReadOnlyList<CredentialRef>> ActiveCredentialsAsync(int clientId, CancellationToken ct = default);
    Task<(IReadOnlyList<ReservationDto> Reservations, IReadOnlyList<object> Errors)> FetchAllAsync(IReadOnlyList<CredentialRef> credentials, int alertDays, bool includeUtilization, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, object?>> ListClientReservationsAsync(IReadOnlyList<CredentialRef> credentials, int alertDays, bool includeUtilization, CancellationToken ct = default);
}

public sealed class ReservationService(ISqlConnectionFactory factory, IAzureReservationsClient client) : IReservationService
{
    public async Task<IReadOnlyList<CredentialRef>> ActiveCredentialsAsync(int clientId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT credential_id, credential_name FROM dbo.client_azure_credentials
            WHERE client_id = @cid AND is_active = 1 ORDER BY created_at DESC
            """;
        cmd.Parameters.Add(new SqlParameter("@cid", clientId));
        var list = new List<CredentialRef>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new CredentialRef(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1)));
        return list;
    }

    public async Task<(IReadOnlyList<ReservationDto> Reservations, IReadOnlyList<object> Errors)> FetchAllAsync(
        IReadOnlyList<CredentialRef> credentials, int alertDays, bool includeUtilization, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var byId = new Dictionary<string, ReservationDto>();
        var errors = new List<object>();

        foreach (var cred in credentials)
        {
            IReadOnlyList<ReservationDto> reservations;
            try { reservations = await client.FetchForCredentialAsync(cred.CredentialId, alertDays, today, includeUtilization, ct); }
            catch (Exception ex)
            {
                var msg = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
                errors.Add(new { credential_id = cred.CredentialId, credential_name = cred.CredentialName, error = $"{ex.GetType().Name}: {msg}" });
                continue;
            }
            foreach (var res in reservations)
            {
                var key = res.ReservationId ?? $"{cred.CredentialId}:{res.Name}";
                byId.TryAdd(key, res);
            }
        }
        var ordered = byId.Values.OrderBy(r => r.DaysRemaining).ToList();
        return (ordered, errors);
    }

    public async Task<IReadOnlyDictionary<string, object?>> ListClientReservationsAsync(
        IReadOnlyList<CredentialRef> credentials, int alertDays, bool includeUtilization, CancellationToken ct = default)
    {
        var (ordered, errors) = await FetchAllAsync(credentials, alertDays, includeUtilization, ct);
        return new Dictionary<string, object?>
        {
            ["alert_days"] = alertDays,
            ["total"] = ordered.Count,
            ["expiring_count"] = ordered.Count(r => r.Expiring),
            ["expired_count"] = ordered.Count(r => r.Expired),
            ["reservations"] = ordered,
            ["errors"] = errors,
        };
    }
}
