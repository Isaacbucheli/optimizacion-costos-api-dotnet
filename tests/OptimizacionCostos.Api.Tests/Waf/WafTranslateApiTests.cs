using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.Waf;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Waf;

public sealed class WafTranslateApiTests : IClassFixture<WafTranslateApiTests.Factory>
{
    private readonly Factory _factory;
    public WafTranslateApiTests(Factory factory) => _factory = factory;

    private HttpClient ClientFor(string email, string role)
    {
        var client = _factory.CreateClient();
        _factory.Directory.Add(email, role);
        var token = BitJwt.Create(Factory.Secret, email, "Test User", role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Translate_SinToken_401()
    {
        var res = await _factory.CreateClient().PostAsync("/waf/translate",
            Json("{\"target\":\"en\",\"items\":[{\"key\":\"a\",\"text\":\"Hola\"}]}"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Translate_Admin_DevuelveTraduccion()
    {
        _factory.Translation.Configured = true;
        var res = await ClientFor("a@bit.ec", Roles.Admin).PostAsync("/waf/translate",
            Json("{\"target\":\"en\",\"items\":[{\"key\":\"a\",\"text\":\"Hola\"}]}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("EN::Hola", body);
    }

    [Fact]
    public async Task Translate_ItemsVacios_400()
    {
        var res = await ClientFor("a@bit.ec", Roles.Admin).PostAsync("/waf/translate",
            Json("{\"target\":\"en\",\"items\":[]}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Translate_SinConfig_503()
    {
        _factory.Translation.Configured = false;
        var res = await ClientFor("a@bit.ec", Roles.Admin).PostAsync("/waf/translate",
            Json("{\"target\":\"en\",\"items\":[{\"key\":\"a\",\"text\":\"Hola\"}]}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public const string Secret = TestAppFactory.Secret;
        public FakeUserDirectory Directory { get; } = new();
        public FakeTranslation Translation { get; } = new();

        public Factory() => Environment.SetEnvironmentVariable("JWT_SECRET", Secret);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserDirectory>();
                services.AddSingleton<IUserDirectory>(Directory);
                services.RemoveAll<IWafTranslationService>();
                services.AddSingleton<IWafTranslationService>(Translation);
                services.RemoveAll<IModulePermissionStore>();
                services.AddSingleton<IModulePermissionStore>(new FakeModulePermissionStore().SeedDefaults());
            });
        }
    }

    public sealed class FakeTranslation : IWafTranslationService
    {
        public bool Configured { get; set; } = true;
        public bool IsConfigured => Configured;
        public Task<IReadOnlyList<WafTranslationItem>> TranslateAsync(
            string target, IReadOnlyList<WafTranslationItem> items, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WafTranslationItem>>(
                items.Select(i => new WafTranslationItem(i.Key, $"EN::{i.Text}")).ToList());
    }
}
