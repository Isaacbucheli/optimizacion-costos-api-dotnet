using Microsoft.Extensions.Configuration;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Tests.Auth;

/// <summary>
/// Defaults de sesión de la spec DAST 2026-08-19: access de 15 minutos (antes 480 — el
/// rollout se gobierna con el app setting APP_AUTH_TOKEN_MINUTES en Azure), familia de
/// refresh de 8 horas (la jornada actual) y gracia de reuso de 60 segundos.
/// </summary>
public class AuthConfigTests
{
    [Fact]
    public void Los_defaults_de_sesion_son_los_de_la_spec()
    {
        var tokenPrev = Environment.GetEnvironmentVariable("APP_AUTH_TOKEN_MINUTES");
        var refreshPrev = Environment.GetEnvironmentVariable("APP_AUTH_REFRESH_HOURS");
        var gracePrev = Environment.GetEnvironmentVariable("APP_AUTH_REFRESH_GRACE_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("APP_AUTH_TOKEN_MINUTES", null);
            Environment.SetEnvironmentVariable("APP_AUTH_REFRESH_HOURS", null);
            Environment.SetEnvironmentVariable("APP_AUTH_REFRESH_GRACE_SECONDS", null);

            var config = AppConfig.FromConfiguration(new ConfigurationBuilder().Build());

            Assert.Equal(15, config.AuthTokenMinutes);
            Assert.Equal(8, config.AuthRefreshHours);
            Assert.Equal(60, config.AuthRefreshGraceSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_AUTH_TOKEN_MINUTES", tokenPrev);
            Environment.SetEnvironmentVariable("APP_AUTH_REFRESH_HOURS", refreshPrev);
            Environment.SetEnvironmentVariable("APP_AUTH_REFRESH_GRACE_SECONDS", gracePrev);
        }
    }

    [Fact]
    public void Las_env_vars_ganan_sobre_los_defaults()
    {
        var tokenPrev = Environment.GetEnvironmentVariable("APP_AUTH_TOKEN_MINUTES");
        var refreshPrev = Environment.GetEnvironmentVariable("APP_AUTH_REFRESH_HOURS");
        var gracePrev = Environment.GetEnvironmentVariable("APP_AUTH_REFRESH_GRACE_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("APP_AUTH_TOKEN_MINUTES", "480");
            Environment.SetEnvironmentVariable("APP_AUTH_REFRESH_HOURS", "12");
            Environment.SetEnvironmentVariable("APP_AUTH_REFRESH_GRACE_SECONDS", "30");

            var config = AppConfig.FromConfiguration(new ConfigurationBuilder().Build());

            Assert.Equal(480, config.AuthTokenMinutes);
            Assert.Equal(12, config.AuthRefreshHours);
            Assert.Equal(30, config.AuthRefreshGraceSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_AUTH_TOKEN_MINUTES", tokenPrev);
            Environment.SetEnvironmentVariable("APP_AUTH_REFRESH_HOURS", refreshPrev);
            Environment.SetEnvironmentVariable("APP_AUTH_REFRESH_GRACE_SECONDS", gracePrev);
        }
    }
}
