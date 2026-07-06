using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using OptimizacionCostos.Api.Features.AzureIntegration.UserSessions;

namespace OptimizacionCostos.Api.Tests.AzureIntegration.UserSessions;

/// <summary>Device flow controlado por el test: emite el prompt y completa/falla a demanda.</summary>
public sealed class FakeDeviceCodeFlow : IDeviceCodeFlow
{
    public TaskCompletionSource<DeviceFlowResult> Tcs { get; private set; } = new();
    public DeviceCodePrompt Prompt { get; set; } = new("TESTCODE", "https://login.microsoft.com/device", "usa TESTCODE");

    public Task<DeviceFlowResult> AuthenticateAsync(Action<DeviceCodePrompt> onPrompt, CancellationToken ct)
    {
        onPrompt(Prompt);
        ct.Register(() => Tcs.TrySetCanceled(ct));
        return Tcs.Task;
    }

    public void Reset() => Tcs = new();
}

/// <summary>Credencial fake que registra los contextos pedidos y devuelve un token fijo.</summary>
public sealed class FakeTokenCredential(AccessToken token) : TokenCredential
{
    public List<TokenRequestContext> Requests { get; } = [];
    public Func<TokenRequestContext, AccessToken>? OnGetToken { get; set; }

    public override AccessToken GetToken(TokenRequestContext ctx, CancellationToken ct)
    { Requests.Add(ctx); return OnGetToken?.Invoke(ctx) ?? token; }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext ctx, CancellationToken ct)
        => new(GetToken(ctx, ct));
}

public class AzureUserSessionServiceTests
{
    private static AccessToken Token(int minutes = 60) =>
        new("tok-" + Guid.NewGuid().ToString("N")[..8], DateTimeOffset.UtcNow.AddMinutes(minutes));

    private static async Task WaitFor(Func<bool> cond, int ms = 2000)
    {
        var start = Environment.TickCount;
        while (!cond() && Environment.TickCount - start < ms) await Task.Delay(10);
        Assert.True(cond(), "condición no alcanzada a tiempo");
    }

    [Fact]
    public async Task Start_emite_prompt_y_autentica_al_completar()
    {
        var flow = new FakeDeviceCodeFlow();
        var svc = new AzureUserSessionService(flow, NullLogger<AzureUserSessionService>.Instance);

        var snap = svc.Start("isaac@bit.ec");
        Assert.Equal("pending_device", snap.Status);

        await WaitFor(() => svc.GetStatus("isaac@bit.ec")?.UserCode == "TESTCODE");

        flow.Tcs.SetResult(new DeviceFlowResult(new FakeTokenCredential(Token()), Token(), "isaac@grupobusiness.it"));
        await WaitFor(() => svc.GetStatus("isaac@bit.ec")?.Status == "authenticated");
        Assert.Equal("isaac@grupobusiness.it", svc.GetStatus("isaac@bit.ec")!.AzureUpn);
    }

    [Fact]
    public async Task Fallo_del_flow_deja_sesion_failed_con_error()
    {
        var flow = new FakeDeviceCodeFlow();
        var svc = new AzureUserSessionService(flow, NullLogger<AzureUserSessionService>.Instance);
        svc.Start("isaac@bit.ec");
        flow.Tcs.SetException(new InvalidOperationException("AADSTS bloqueado"));
        await WaitFor(() => svc.GetStatus("isaac@bit.ec")?.Status == "failed");
        Assert.Contains("bloqueado", svc.GetStatus("isaac@bit.ec")!.Error);
    }

    [Fact]
    public void GetStatus_sin_sesion_devuelve_null_y_credencial_lanza()
    {
        var svc = new AzureUserSessionService(new FakeDeviceCodeFlow(), NullLogger<AzureUserSessionService>.Instance);
        Assert.Null(svc.GetStatus("nadie@bit.ec"));
        Assert.Throws<UserSessionExpiredException>(() => svc.GetCredentialForEmail("nadie@bit.ec"));
    }

    [Fact]
    public async Task Disconnect_borra_y_Start_reemplaza_sesion()
    {
        var flow = new FakeDeviceCodeFlow();
        var svc = new AzureUserSessionService(flow, NullLogger<AzureUserSessionService>.Instance);
        svc.Start("isaac@bit.ec");
        flow.Tcs.SetResult(new DeviceFlowResult(new FakeTokenCredential(Token()), Token(), "x@y"));
        await WaitFor(() => svc.GetStatus("isaac@bit.ec")?.Status == "authenticated");

        svc.Disconnect("isaac@bit.ec");
        Assert.Null(svc.GetStatus("isaac@bit.ec"));

        flow.Reset();
        var snap = svc.Start("isaac@bit.ec");
        Assert.Equal("pending_device", snap.Status);
    }

    [Fact]
    public async Task Credencial_de_sesion_autenticada_no_lanza()
    {
        var flow = new FakeDeviceCodeFlow();
        var svc = new AzureUserSessionService(flow, NullLogger<AzureUserSessionService>.Instance);
        svc.Start("isaac@bit.ec");
        flow.Tcs.SetResult(new DeviceFlowResult(new FakeTokenCredential(Token()), Token(), null));
        await WaitFor(() => svc.GetStatus("isaac@bit.ec")?.Status == "authenticated");
        Assert.NotNull(svc.GetCredentialForEmail("isaac@bit.ec"));
    }

    [Fact]
    public async Task Disconnect_invalida_credenciales_ya_entregadas()
    {
        var flow = new FakeDeviceCodeFlow();
        var svc = new AzureUserSessionService(flow, NullLogger<AzureUserSessionService>.Instance);
        svc.Start("isaac@bit.ec");
        flow.Tcs.SetResult(new DeviceFlowResult(new FakeTokenCredential(Token()), Token(), null));
        await WaitFor(() => svc.GetStatus("isaac@bit.ec")?.Status == "authenticated");

        var cred = svc.GetCredentialForEmail("isaac@bit.ec");
        svc.Disconnect("isaac@bit.ec");

        await Assert.ThrowsAsync<UserSessionExpiredException>(
            async () => await cred.GetTokenAsync(new TokenRequestContext(["x"]), default));
    }
}
