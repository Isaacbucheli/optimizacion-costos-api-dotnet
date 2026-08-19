using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.Identity;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Contratos HTTP del preview de importación de matriz Excel WAF que se resuelven ANTES de
/// tocar la BD. El guard de acceso corre primero que la validación de extensión: con el orden
/// inverso, el error que recibe un usuario sin permiso depende de cómo se llame su archivo
/// (fuga de información, mismo contrato que InformeValorUploadApiTests). Los caminos que sí
/// requieren BD (413 por tamaño, 422 sin encabezado con cliente real) se verifican en el E2E
/// local contra la BD -valida; la lógica del 422 está cubierta en WafExcelImporterParseTests.
/// </summary>
public sealed class WafExcelPreviewApiTests : IClassFixture<WafExcelPreviewApiTests.Factory>
{
    private readonly Factory _factory;
    public WafExcelPreviewApiTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string email, string role)
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        var token = BitJwt.Create(Factory.Secret, email, "Test User", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static MultipartFormDataContent Multipart(string fileName, byte[] bytes)
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(part, "file", fileName);
        return content;
    }

    [Fact]
    public async Task Sin_acceso_al_cliente_la_extension_invalida_igual_devuelve_403()
    {
        // Consultor sin clientes asignados (el fake de acceso niega todo por defecto) y un
        // archivo que ni siquiera es .xlsx: la respuesta debe ser 403 (acceso), no 400
        // (extensión) — el error no puede depender del nombre del archivo.
        var client = ClientFor("qa.sin.cartera@bit.test", "consultor");
        using var body = Multipart("cualquiera.txt", "no soy excel"u8.ToArray());

        var res = await client.PostAsync("/waf/clients/7/excel-import/preview", body);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeWafAccessDenyAll Access { get; } = new();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IAnalysisAccess>();
                services.AddSingleton<IAnalysisAccess>(Access);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
            });
        }
    }

    /// <summary>Acceso que niega todos los clientes: el flujo del test debe cortarse en el
    /// guard, sin llegar jamás a la BD (los métodos no usados revientan a propósito).</summary>
    public sealed class FakeWafAccessDenyAll : IAnalysisAccess
    {
        public Task<AccessCheck> AssertClientAccessAsync(ClaimsPrincipal user, int clientId, CancellationToken ct = default)
            => Task.FromResult(AccessCheck.Forbidden());

        public Task<IReadOnlySet<int>?> AccessibleClientIdsAsync(ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<int>?>(new HashSet<int>());

        public Task<AccessCheck> AssertAnalysisAccessAsync(ClaimsPrincipal user, int analysisId, CancellationToken ct = default)
            => throw new InvalidOperationException("no debería llamarse en estos tests");

        public Task<AccessCheck> AssertCostResultAccessAsync(ClaimsPrincipal user, int costResultId, CancellationToken ct = default)
            => throw new InvalidOperationException("no debería llamarse en estos tests");

        public Task<AccessCheck> AssertFileAccessAsync(ClaimsPrincipal user, int fileId, CancellationToken ct = default)
            => throw new InvalidOperationException("no debería llamarse en estos tests");
    }
}
