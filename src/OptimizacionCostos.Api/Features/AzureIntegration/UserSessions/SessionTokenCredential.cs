using Azure.Core;

namespace OptimizacionCostos.Api.Features.AzureIntegration.UserSessions;

public sealed class SessionTokenCredential(SessionHandle session, string email) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext ctx, CancellationToken ct)
        => GetTokenAsync(ctx, ct).AsTask().GetAwaiter().GetResult();

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext ctx, CancellationToken ct)
    {
        lock (session.Lock)
        {
            if (session.State != UserSessionState.Authenticated) throw new UserSessionExpiredException(email);
            return new(session.Token);
        }
    }
}
