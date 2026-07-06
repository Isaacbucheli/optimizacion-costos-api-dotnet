using System.Collections.Concurrent;
using Azure.Core;

namespace OptimizacionCostos.Api.Features.AzureIntegration.UserSessions;

public interface IAzureUserSessionService
{
    UserSessionSnapshot Start(string email);
    UserSessionSnapshot? GetStatus(string email);
    void Disconnect(string email);
    /// <summary>Credencial reusable de la sesión. Lanza <see cref="UserSessionExpiredException"/> si no hay sesión autenticada.</summary>
    TokenCredential GetCredentialForEmail(string email);
}

/// <summary>
/// Sesiones Azure por usuario de app (email del JWT). Singleton en memoria: una sesión
/// activa por usuario; reinicio del worker = reconectar (mismo trade-off que ReportGeneration).
/// </summary>
public sealed class AzureUserSessionService(IDeviceCodeFlow flow, ILogger<AzureUserSessionService> logger)
    : IAzureUserSessionService
{
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, SessionHandle> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public UserSessionSnapshot Start(string email)
    {
        var session = new SessionHandle { State = UserSessionState.PendingDevice };
        _sessions[email] = session; // reemplaza cualquier sesión anterior

        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(AuthTimeout);
            try
            {
                var result = await flow.AuthenticateAsync(p =>
                {
                    lock (session.Lock) { session.UserCode = p.UserCode; session.VerificationUrl = p.VerificationUrl; }
                }, cts.Token);
                lock (session.Lock)
                {
                    session.Inner = result.Credential;
                    session.Token = result.FirstToken;
                    session.AzureUpn = result.Upn;
                    session.State = UserSessionState.Authenticated;
                }
                logger.LogInformation("Sesión Azure autenticada app_user={Email} upn={Upn}", email, result.Upn);
            }
            catch (Exception ex)
            {
                lock (session.Lock)
                {
                    session.State = UserSessionState.Failed;
                    session.Error = ex is OperationCanceledException
                        ? "Tiempo de espera agotado (15 min); vuelve a iniciar la conexión."
                        : ex.Message;
                }
                logger.LogWarning("Device flow falló app_user={Email} type={Type}", email, ex.GetType().Name);
            }
        });
        return Snapshot(session);
    }

    public UserSessionSnapshot? GetStatus(string email)
        => _sessions.TryGetValue(email, out var s) ? Snapshot(s) : null;

    public void Disconnect(string email) => _sessions.TryRemove(email, out _);

    public TokenCredential GetCredentialForEmail(string email)
    {
        if (!_sessions.TryGetValue(email, out var s))
            throw new UserSessionExpiredException(email);
        lock (s.Lock)
        {
            if (s.State != UserSessionState.Authenticated)
                throw new UserSessionExpiredException(email);
        }
        return new SessionTokenCredential(s, email);
    }

    private static UserSessionSnapshot Snapshot(SessionHandle s)
    {
        lock (s.Lock)
        {
            var status = s.State switch
            {
                UserSessionState.PendingDevice => "pending_device",
                UserSessionState.Authenticated => "authenticated",
                UserSessionState.Failed => "failed",
                _ => "expired",
            };
            return new UserSessionSnapshot(status, s.UserCode, s.VerificationUrl, s.AzureUpn, s.Error);
        }
    }
}
