using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.Clients;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.Optimization;
using OptimizacionCostos.Api.Tests.CostEngine.Api; // FakeAnalysisAccess

namespace OptimizacionCostos.Api.Tests.Optimization;

/// <summary>
/// Cableado del enfriamiento en el barrido de optimización. El cálculo se prueba en
/// <see cref="Tests.Data.CooldownWindowTests"/>; acá se verifica lo que puede estar mal en el endpoint:
/// el código de respuesta, el Retry-After, y sobre todo **el orden** respecto del control de acceso.
///
/// Ese orden es lo que más importa: si el enfriamiento se evaluara antes, un 429 sobre un cliente ajeno
/// revelaría que ese cliente existe y que alguien lo barrió hace poco. El control se volvería una fuga.
/// </summary>
public sealed class OptimizationScanCooldownTests : IClassFixture<OptimizationScanCooldownTests.Factory>
{
    private readonly Factory _factory;
    public OptimizationScanCooldownTests(Factory factory) => _factory = factory;

    private HttpClient ClienteComo(string email, string rol)
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, rol);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", BitJwt.Create(Factory.Secret, email, "Test User", rol));
        return client;
    }

    private HttpClient Admin() => ClienteComo("admin@bit.ec", Roles.Admin);

    [Fact]
    public async Task Sin_enfriamiento_el_barrido_corre()
    {
        _factory.Cooldown.Falta = null;
        _factory.Svc.ScanCalls = 0;

        var res = await Admin().PostAsync("/optimization/clients/7/scan", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, _factory.Svc.ScanCalls);
    }

    [Fact]
    public async Task En_enfriamiento_responde_429_con_retry_after_y_sin_correr_el_barrido()
    {
        _factory.Cooldown.Falta = TimeSpan.FromMinutes(4);
        _factory.Svc.ScanCalls = 0;

        var res = await Admin().PostAsync("/optimization/clients/7/scan", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal(240, res.Headers.RetryAfter?.Delta?.TotalSeconds);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("barrido de optimización", body.GetProperty("detail").GetString()!);
        Assert.Equal(240, body.GetProperty("retry_after_seconds").GetInt32());

        // Lo esencial: el trabajo contra Azure no se ejecutó.
        Assert.Equal(0, _factory.Svc.ScanCalls);
    }

    [Fact]
    public async Task Una_espera_larga_se_expresa_en_minutos()
    {
        // "en 9 minutos" se lee mejor que "en 540 segundos" para el consultor.
        _factory.Cooldown.Falta = TimeSpan.FromSeconds(540);

        var res = await Admin().PostAsync("/optimization/clients/7/scan", null);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains("9 minutos", body.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task Sin_acceso_al_cliente_responde_403_y_no_se_consulta_el_enfriamiento()
    {
        // Consultor sin asignaciones: no puede ver el cliente 999. El enfriamiento se evalúa DESPUÉS
        // del control de acceso, así que ni se pregunta.
        _factory.Cooldown.Falta = TimeSpan.FromMinutes(4);
        _factory.Cooldown.Llamadas = 0;
        _factory.Svc.ScanCalls = 0;

        var res = await ClienteComo("sin-cartera@bit.ec", Roles.Consultor)
            .PostAsync("/optimization/clients/999/scan", null);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(0, _factory.Cooldown.Llamadas);
        Assert.Equal(0, _factory.Svc.ScanCalls);
    }

    [Fact]
    public async Task El_enfriamiento_se_pide_por_cliente_y_con_una_clave_estable()
    {
        // La clave identifica la operación en dbo.operation_cooldown. Si cambia, los registros previos
        // quedan huérfanos y el enfriamiento se reinicia sin que nadie lo note.
        _factory.Cooldown.Falta = null;

        await Admin().PostAsync("/optimization/clients/42/scan", null);

        Assert.Equal("optimization-scan", _factory.Cooldown.UltimaClave);
        Assert.Equal(42, _factory.Cooldown.UltimoCliente);
    }

    // ---- fakes ----

    public sealed class FakeCooldown : IOperationCooldown
    {
        public TimeSpan? Falta { get; set; }
        public int Llamadas { get; set; }
        public string? UltimaClave { get; private set; }
        public int? UltimoCliente { get; private set; }

        public Task<TimeSpan?> TryBeginAsync(string operationKey, int? clientId, TimeSpan cooldown, CancellationToken ct)
        {
            Llamadas++;
            UltimaClave = operationKey;
            UltimoCliente = clientId;
            return Task.FromResult(Falta);
        }
    }

    /// <summary>Cuenta las corridas: el fake compartido lanza NotSupportedException en RunScanAsync.</summary>
    public sealed class FakeScanService : IOptimizationService
    {
        public int ScanCalls { get; set; }

        public bool AccessAllowed(string? email) => true;
        public Task<IReadOnlyDictionary<string, object?>> RunScanAsync(int clientId, string? actor, CancellationToken ct = default)
        {
            ScanCalls++;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?> { ["scan_id"] = 1, ["findings"] = 0 });
        }

        public Task<int?> ScanOwnerAsync(int scanId, CancellationToken ct = default) => Task.FromResult<int?>(null);
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListScansAsync(int clientId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ScanFindingsAsync(int scanId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
        public Task<int?> FindingStateOwnerAsync(byte[] fingerprint, CancellationToken ct = default) => Task.FromResult<int?>(null);
        public Task<bool> UpdateStateAsync(byte[] fingerprint, string state, string? notes, string? actor, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = "test-secret-con-mas-de-32-caracteres-1234567890";

        public FakeUserDirectory Directory { get; } = new();
        public FakeScanService Svc { get; } = new();
        public FakeCooldown Cooldown { get; } = new();
        // Sin asignaciones: admin tiene acceso global, cualquier otro rol queda fuera de todo cliente.
        public FakeAnalysisAccess Access { get; } = new();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>(); services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IAnalysisAccess>(); services.AddSingleton<IAnalysisAccess>(Access);
                services.RemoveAll<IOptimizationService>(); services.AddSingleton<IOptimizationService>(Svc);
                services.RemoveAll<IClientStore>(); services.AddSingleton<IClientStore>(new FakeClientStoreForExcel());
                services.RemoveAll<IOperationCooldown>(); services.AddSingleton<IOperationCooldown>(Cooldown);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
            });
        }
    }
}
