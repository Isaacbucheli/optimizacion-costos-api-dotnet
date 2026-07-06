using OptimizacionCostos.Api.Configuration;
using Microsoft.Extensions.Configuration;

namespace OptimizacionCostos.Api.Tests.AzureIntegration.UserSessions;

public class AppConfigUserSessionTests
{
    private static AppConfig Build(Dictionary<string, string?> env)
    {
        // AppConfig prioriza variables de entorno; fijarlas y restaurarlas por test.
        var originals = env.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v);
            return AppConfig.FromConfiguration(new ConfigurationBuilder().Build());
        }
        finally
        {
            foreach (var (k, v) in originals) Environment.SetEnvironmentVariable(k, v);
        }
    }

    [Fact]
    public void UserSession_defaults_are_safe()
    {
        var cfg = Build(new()
        {
            ["USER_SESSION_AUTH_ENABLED"] = null,
            ["USER_SESSION_CLIENT_ID"] = null,
            ["USER_SESSION_TENANT_ID"] = null,
            ["USER_SESSION_ALLOWED_EMAILS"] = null,
        });
        Assert.False(cfg.UserSessionAuthEnabled);
        Assert.Equal("04b07795-8ddb-461a-bbee-02f9e1bf7b46", cfg.UserSessionClientId);
        Assert.Equal("24884996-6863-4925-a1ba-f1a160b581e2", cfg.UserSessionTenantId);
        Assert.Empty(cfg.UserSessionAllowedEmails);
    }

    [Fact]
    public void UserSession_env_overrides_apply()
    {
        var cfg = Build(new()
        {
            ["USER_SESSION_AUTH_ENABLED"] = "true",
            ["USER_SESSION_CLIENT_ID"] = "otro-client",
            ["USER_SESSION_TENANT_ID"] = "otro-tenant",
            ["USER_SESSION_ALLOWED_EMAILS"] = "a@bit.ec, b@bit.ec",
        });
        Assert.True(cfg.UserSessionAuthEnabled);
        Assert.Equal("otro-client", cfg.UserSessionClientId);
        Assert.Equal("otro-tenant", cfg.UserSessionTenantId);
        Assert.Equal(new[] { "a@bit.ec", "b@bit.ec" }, cfg.UserSessionAllowedEmails);
    }
}
