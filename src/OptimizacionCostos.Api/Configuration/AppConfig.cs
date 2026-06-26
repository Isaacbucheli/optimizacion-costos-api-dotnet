namespace OptimizacionCostos.Api.Configuration;

/// <summary>
/// Configuracion de la API. Lee las MISMAS variables de entorno que el backend
/// FastAPI (SQL_*, JWT_SECRET, ...) para poder coexistir contra la misma BD y
/// validar los mismos tokens que emite el login actual.
/// </summary>
public sealed class AppConfig
{
    public string SqlServer { get; init; } = "";
    public string SqlDatabase { get; init; } = "";
    public string SqlUsername { get; init; } = "";
    public string SqlPassword { get; init; } = "";

    public string JwtSecret { get; init; } = "";
    public int AuthTokenMinutes { get; init; } = 480;

    public string[] CorsOrigins { get; init; } = [];

    public static AppConfig FromConfiguration(IConfiguration cfg)
    {
        // Prioridad: variable de entorno (paridad con FastAPI) y, si no, appsettings.
        string Get(string key, string fallback = "") =>
            Environment.GetEnvironmentVariable(key) ?? cfg[key] ?? fallback;

        var minutesRaw = Get("APP_AUTH_TOKEN_MINUTES", "480");
        var corsRaw = Get("CORS_ORIGINS");

        return new AppConfig
        {
            SqlServer = Get("SQL_SERVER"),
            SqlDatabase = Get("SQL_DATABASE"),
            SqlUsername = Get("SQL_USERNAME"),
            SqlPassword = Get("SQL_PASSWORD"),
            JwtSecret = Get("JWT_SECRET"),
            AuthTokenMinutes = int.TryParse(minutesRaw, out var m) ? m : 480,
            CorsOrigins = corsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
    }
}
