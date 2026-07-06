using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Features.AzureIntegration.UserSessions;

public sealed record DeviceCodePrompt(string UserCode, string VerificationUrl, string Message);
public sealed record DeviceFlowResult(TokenCredential Credential, AccessToken FirstToken, string? Upn);

/// <summary>Seam del device code flow (fake en tests; Azure.Identity en producción).</summary>
public interface IDeviceCodeFlow
{
    Task<DeviceFlowResult> AuthenticateAsync(Action<DeviceCodePrompt> onPrompt, CancellationToken ct);
}

public sealed class AzureDeviceCodeFlow(AppConfig config) : IDeviceCodeFlow
{
    public const string ArmScope = "https://management.azure.com/.default";

    public async Task<DeviceFlowResult> AuthenticateAsync(Action<DeviceCodePrompt> onPrompt, CancellationToken ct)
    {
        var credential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
        {
            TenantId = config.UserSessionTenantId,
            ClientId = config.UserSessionClientId,
            DeviceCodeCallback = (info, _) =>
            {
                onPrompt(new DeviceCodePrompt(info.UserCode, info.VerificationUri.ToString(), info.Message));
                return Task.CompletedTask;
            },
        });
        // Contexto canónico (scope ARM + tenant): el mismo que usará SessionTokenCredential.
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([ArmScope], tenantId: config.UserSessionTenantId), ct);
        return new DeviceFlowResult(credential, token, JwtClaims.TryGetUpn(token.Token));
    }
}

/// <summary>Lee upn/unique_name del payload del access token SIN validar firma (solo para UI).</summary>
public static class JwtClaims
{
    public static string? TryGetUpn(string accessToken)
    {
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var node = JsonNode.Parse(Convert.FromBase64String(payload));
            return node?["upn"]?.GetValue<string>() ?? node?["unique_name"]?.GetValue<string>();
        }
        catch { return null; }
    }
}
