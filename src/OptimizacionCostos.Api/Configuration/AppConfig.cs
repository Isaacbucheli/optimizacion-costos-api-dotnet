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
    // Habilita POST /auth/bootstrap (crear primer admin). Default false (paridad con FastAPI).
    public bool AuthBootstrapEnabled { get; init; }

    // Lista blanca de emails con acceso al módulo Optimización (B6). Vacía = abierto a todos.
    public string[] OptimizationAllowedEmails { get; init; } = [];

    // Sesiones Azure con cuenta de usuario (Lighthouse, clientes temporales). Ver spec 2026-07-06.
    // Flag default false; client id default = Azure CLI first-party (sin app registration propio).
    public bool UserSessionAuthEnabled { get; init; }
    public string UserSessionClientId { get; init; } = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    public string UserSessionTenantId { get; init; } = "24884996-6863-4925-a1ba-f1a160b581e2";
    // Vacía = solo rol admin puede usar el feature; con emails = esos usuarios + admins.
    public string[] UserSessionAllowedEmails { get; init; } = [];

    // Scheduler semanal de Advisor Score (B7/WAF). Default deshabilitado (lo activa el env en prod).
    public bool AdvisorScoreSchedulerEnabled { get; init; }
    public string AdvisorScoreSchedulerTz { get; init; } = "America/Bogota";
    public string AdvisorScoreSchedulerTime { get; init; } = "06:00";
    public int AdvisorScoreSchedulerWeekday { get; init; } = 0; // 0=lunes (datetime.weekday())

    public string[] CorsOrigins { get; init; } = [];

    // Key Vault — guarda los client_secret de las credenciales Azure de los clientes.
    // Misma variable que el FastAPI (KEY_VAULT_URL). La identidad del App Service
    // (DefaultAzureCredential) debe poder leer/escribir secretos en este vault.
    public string KeyVaultUrl { get; init; } = "";

    // Blob Storage — logos de cliente, plantillas Excel, salidas (informes/Excel). Mismas
    // variables que el FastAPI. La identidad necesita "Storage Blob Data Contributor".
    public string StorageAccountName { get; init; } = "";
    public string StorageContainerUploads { get; init; } = "uploads";
    public string StorageContainerOutputs { get; init; } = "outputs";
    public string StorageContainerTemplates { get; init; } = "templates";
    public string TemplateFileName { get; init; } = "BANISI-Optimizacion_Costos_V2.xlsx";

    // Azure OpenAI — asistente de precios (fallback cuando la selección determinista no halla).
    // Mismas variables que el FastAPI para coexistir. El asistente solo elige de candidatos
    // REALES del Retail; nunca inventa. Gateado por AzureOpenAiEnabled + claves + PricingAssistMode.
    public bool AzureOpenAiEnabled { get; init; }
    public string AzureOpenAiEndpoint { get; init; } = "";
    public string AzureOpenAiApiKey { get; init; } = "";
    public string AzureOpenAiDeployment { get; init; } = "";
    public string AzureOpenAiApiVersion { get; init; } = "2025-04-01-preview";
    public string PricingAssistMode { get; init; } = "assist_match";

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
            KeyVaultUrl = Get("KEY_VAULT_URL"),
            StorageAccountName = Get("STORAGE_ACCOUNT_NAME"),
            StorageContainerUploads = Get("STORAGE_CONTAINER_UPLOADS", "uploads"),
            StorageContainerOutputs = Get("STORAGE_CONTAINER_OUTPUTS", "outputs"),
            StorageContainerTemplates = Get("STORAGE_CONTAINER_TEMPLATES", "templates"),
            TemplateFileName = Get("TEMPLATE_FILE_NAME", "BANISI-Optimizacion_Costos_V2.xlsx"),
            JwtSecret = Get("JWT_SECRET"),
            AuthTokenMinutes = int.TryParse(minutesRaw, out var m) ? m : 480,
            AuthBootstrapEnabled = Get("APP_AUTH_BOOTSTRAP_ENABLED").Trim().ToLowerInvariant() is "true" or "1",
            OptimizationAllowedEmails = Get("OPTIMIZATION_ALLOWED_EMAILS")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AdvisorScoreSchedulerEnabled = Get("ADVISOR_SCORE_SCHEDULER_ENABLED").Trim().ToLowerInvariant() is "true" or "1",
            AdvisorScoreSchedulerTz = Get("ADVISOR_SCORE_SCHEDULER_TZ", "America/Bogota"),
            AdvisorScoreSchedulerTime = Get("ADVISOR_SCORE_SCHEDULER_TIME", "06:00"),
            AdvisorScoreSchedulerWeekday = int.TryParse(Get("ADVISOR_SCORE_SCHEDULER_WEEKDAY", "0"), out var wd) ? Math.Clamp(wd, 0, 6) : 0,
            CorsOrigins = corsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AzureOpenAiEnabled = Get("AZURE_OPENAI_ENABLED").Trim().ToLowerInvariant() is "true" or "1",
            AzureOpenAiEndpoint = Get("AZURE_OPENAI_ENDPOINT"),
            AzureOpenAiApiKey = Get("AZURE_OPENAI_API_KEY"),
            AzureOpenAiDeployment = Get("AZURE_OPENAI_DEPLOYMENT"),
            AzureOpenAiApiVersion = Get("AZURE_OPENAI_API_VERSION", "2025-04-01-preview"),
            PricingAssistMode = Get("AZURE_OPENAI_PRICING_ASSIST_MODE", "assist_match"),
            UserSessionAuthEnabled = Get("USER_SESSION_AUTH_ENABLED").Trim().ToLowerInvariant() is "true" or "1",
            UserSessionClientId = Get("USER_SESSION_CLIENT_ID", "04b07795-8ddb-461a-bbee-02f9e1bf7b46"),
            UserSessionTenantId = Get("USER_SESSION_TENANT_ID", "24884996-6863-4925-a1ba-f1a160b581e2"),
            UserSessionAllowedEmails = Get("USER_SESSION_ALLOWED_EMAILS")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        };
    }
}
