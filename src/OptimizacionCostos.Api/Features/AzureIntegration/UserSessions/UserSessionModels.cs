using Azure.Core;

namespace OptimizacionCostos.Api.Features.AzureIntegration.UserSessions;

public enum UserSessionState { PendingDevice, Authenticated, Failed, Expired }

/// <summary>La sesión Azure del usuario expiró o no existe (→ el front pide reconectar).</summary>
public sealed class UserSessionExpiredException(string email)
    : Exception($"La sesión Azure de {email} expiró o no existe; vuelve a conectar la cuenta.");

public sealed record UserSessionSnapshot(
    string Status, string? UserCode, string? VerificationUrl, string? AzureUpn, string? Error);

/// <summary>
/// Estado mutable de UNA sesión. El token/credencial viven SOLO aquí (memoria).
/// Todo acceso a campos mutables va bajo <see cref="Lock"/>.
/// </summary>
public sealed class SessionHandle
{
    public readonly object Lock = new();
    public UserSessionState State;
    public string? UserCode;
    public string? VerificationUrl;
    public string? AzureUpn;
    public string? Error;
    public TokenCredential? Inner;   // DeviceCodeCredential real (renovación silenciosa MSAL)
    public AccessToken Token;        // último access token emitido
}
