using Azure.Core;

namespace OptimizacionCostos.Api.Features.AzureIntegration.UserSessions;

/// <summary>
/// Credencial que reusa el token de la sesión y lo renueva SIEMPRE con el contexto canónico
/// (scope ARM, sin importar lo que pida el caller). Hallazgo del spike 2026-07-06:
/// DeviceCodeCredential re-pide código si el contexto de la solicitud no matchea el original.
/// </summary>
public sealed class SessionTokenCredential(SessionHandle session, string email) : TokenCredential
{
    public const string ArmScope = "https://management.azure.com/.default";
    public static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(5);

    public override AccessToken GetToken(TokenRequestContext ctx, CancellationToken ct)
        => GetTokenAsync(ctx, ct).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext _, CancellationToken ct)
    {
        TokenCredential inner;
        lock (session.Lock)
        {
            if (session.State != UserSessionState.Authenticated || session.Inner is null)
                throw new UserSessionExpiredException(email);
            if (session.Token.ExpiresOn - DateTimeOffset.UtcNow > RenewMargin)
                return session.Token;
            inner = session.Inner;
        }

        try
        {
            // Renovación silenciosa (MSAL usa el refresh token en memoria). Contexto canónico.
            var fresh = await inner.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
            lock (session.Lock) session.Token = fresh;
            return fresh;
        }
        catch (Exception ex) when (ex is Azure.Identity.AuthenticationFailedException
                                      or Azure.Identity.AuthenticationRequiredException)
        {
            lock (session.Lock) session.State = UserSessionState.Expired;
            throw new UserSessionExpiredException(email);
        }
    }
}
