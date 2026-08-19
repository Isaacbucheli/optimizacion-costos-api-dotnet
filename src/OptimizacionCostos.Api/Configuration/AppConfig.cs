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

    // Segunda BD (SQL_*2): tablero de pendientes/bloqueantes "Seguimiento CDC". Vive en OTRO servidor
    // Azure SQL y la administra otro sistema (la SWA del tablero, que sigue en uso), así que la app
    // solo lee y escribe filas: nunca hace DDL. Si faltan, el módulo queda apagado (503), no roto.
    public string Sql2Server { get; init; } = "";
    public string Sql2Database { get; init; } = "";
    public string Sql2Username { get; init; } = "";
    public string Sql2Password { get; init; } = "";

    public string JwtSecret { get; init; } = "";
    // Access token de 15 min por defecto (spec DAST 2026-08-19; antes 480). El rollout se
    // gobierna con el app setting: se mantiene 480 en Azure hasta que el front con refresh
    // esté desplegado, y recién entonces se baja o se quita el setting.
    public int AuthTokenMinutes { get; init; } = 15;
    // Expiración ABSOLUTA de la familia de refresh tokens desde el login (la jornada de
    // siempre): renovar el access no la extiende.
    public int AuthRefreshHours { get; init; } = 8;
    // Ventana en la que el reuso de un refresh ya rotado se tolera como carrera legítima de
    // dos pestañas; fuera de ella el reuso revoca la familia completa (detección de robo).
    public int AuthRefreshGraceSeconds { get; init; } = 60;
    // Habilita POST /auth/bootstrap (crear primer admin). Default false (paridad con FastAPI).
    public bool AuthBootstrapEnabled { get; init; }

    // Expone /swagger y /swagger/v1/swagger.json. Default FALSE: en producción publicaban toda la
    // superficie de la API a cualquiera sin token (hallazgo de la revisión de seguridad). En
    // Development se habilita solo, sin necesidad del flag; en producción hay que prenderlo a mano
    // y volver a apagarlo. Para verificar un deploy usar /health, no swagger.json.
    public bool SwaggerEnabled { get; init; }

    // Lista blanca de emails con acceso al módulo Optimización (B6). Vacía = abierto a todos.
    public string[] OptimizationAllowedEmails { get; init; } = [];

    // Superadmins protegidos (por email; se lee de SUPERADMIN_EMAILS, misma mecánica que las
    // otras listas). Un superadmin no puede ser eliminado, desactivado, degradado ni editado por
    // OTRO usuario; se auto-repara a rol=admin + activo. Vacía = sin superadmins (default seguro).
    // El correo real va en el .env / docs privados, nunca en código (repos públicos).
    public string[] SuperAdminEmails { get; init; } = [];

    /// <summary>True si el email está en la lista de superadmins protegidos (case-insensitive).</summary>
    public bool IsSuperAdmin(string? email) =>
        !string.IsNullOrWhiteSpace(email) &&
        SuperAdminEmails.Any(e => string.Equals(e, email.Trim(), StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// Peticiones por minuto permitidas a cada usuario autenticado (y a cada IP para el trafico
    /// anonimo). Configurable por variable de entorno a proposito: si el umbral resulta apretado en
    /// uso real se sube desde el portal, sin esperar un deploy. <c>0</c> apaga el limitador, que es la
    /// valvula de escape si algo sale mal en produccion.
    /// </summary>
    public int RateLimitPerMinute { get; init; } = 100;

    /// <summary>
    /// Minutos que deben pasar entre dos consultas a Advisor del MISMO cliente. El limite de tasa
    /// global frena la rafaga, pero con 100 por minuto todavia entran varias sincronizaciones reales;
    /// esta es la regla de negocio: nadie necesita re-consultar Advisor de un cliente seis veces en
    /// cuarenta minutos, y es la unica operacion que escribe en ARM de la suscripcion ajena.
    /// <c>0</c> desactiva el enfriamiento.
    /// </summary>
    public int AdvisorSyncCooldownMinutes { get; init; } = 15;

    /// <summary>
    /// Minutos entre dos ejecuciones de la MISMA operacion externa para el mismo cliente (barrido de
    /// optimizacion, sync del Boletin, revision de accesos). Una sola palanca para las tres en vez de
    /// un setting por endpoint: la razon es la misma en todas, la informacion de origen no cambia en
    /// segundos. <c>0</c> desactiva el enfriamiento.
    ///
    /// A proposito NO cubre las operaciones con re-ejecucion legitima e inmediata (sync de
    /// suscripciones, refresco del score de Advisor, generacion de informes, endpoints de IA): ahi el
    /// enfriamiento romperia trabajo real. Ver IOperationCooldown.
    /// </summary>
    public int OperationCooldownMinutes { get; init; } = 10;

    // Key Vault — guarda los client_secret de las credenciales Azure de los clientes.
    // Misma variable que el FastAPI (KEY_VAULT_URL). La identidad del App Service
    // (DefaultAzureCredential) debe poder leer/escribir secretos en este vault.
    public string KeyVaultUrl { get; init; } = "";

    // Blob Storage — logos de cliente, salidas (informes/Excel). Mismas variables que el FastAPI.
    // La identidad necesita "Storage Blob Data Contributor".
    public string StorageAccountName { get; init; } = "";
    public string StorageContainerUploads { get; init; } = "uploads";
    public string StorageContainerOutputs { get; init; } = "outputs";

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

        var minutesRaw = Get("APP_AUTH_TOKEN_MINUTES", "15");
        var corsRaw = Get("CORS_ORIGINS");

        return new AppConfig
        {
            SqlServer = Get("SQL_SERVER"),
            SqlDatabase = Get("SQL_DATABASE"),
            SqlUsername = Get("SQL_USERNAME"),
            SqlPassword = Get("SQL_PASSWORD"),
            Sql2Server = Get("SQL_SERVER2"),
            Sql2Database = Get("SQL_DATABASE2"),
            Sql2Username = Get("SQL_USERNAME2"),
            Sql2Password = Get("SQL_PASSWORD2"),
            KeyVaultUrl = Get("KEY_VAULT_URL"),
            StorageAccountName = Get("STORAGE_ACCOUNT_NAME"),
            StorageContainerUploads = Get("STORAGE_CONTAINER_UPLOADS", "uploads"),
            StorageContainerOutputs = Get("STORAGE_CONTAINER_OUTPUTS", "outputs"),
            JwtSecret = Get("JWT_SECRET"),
            AuthTokenMinutes = int.TryParse(minutesRaw, out var m) ? m : 15,
            AuthRefreshHours = int.TryParse(Get("APP_AUTH_REFRESH_HOURS"), out var rh) && rh > 0 ? rh : 8,
            AuthRefreshGraceSeconds = int.TryParse(Get("APP_AUTH_REFRESH_GRACE_SECONDS"), out var rg) && rg >= 0 ? rg : 60,
            AuthBootstrapEnabled = Get("APP_AUTH_BOOTSTRAP_ENABLED").Trim().ToLowerInvariant() is "true" or "1",
            SwaggerEnabled = Get("SWAGGER_ENABLED").Trim().ToLowerInvariant() is "true" or "1",
            OptimizationAllowedEmails = Get("OPTIMIZATION_ALLOWED_EMAILS")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            SuperAdminEmails = Get("SUPERADMIN_EMAILS")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.ToLowerInvariant()).ToArray(),
            AdvisorScoreSchedulerEnabled = Get("ADVISOR_SCORE_SCHEDULER_ENABLED").Trim().ToLowerInvariant() is "true" or "1",
            AdvisorScoreSchedulerTz = Get("ADVISOR_SCORE_SCHEDULER_TZ", "America/Bogota"),
            AdvisorScoreSchedulerTime = Get("ADVISOR_SCORE_SCHEDULER_TIME", "06:00"),
            AdvisorScoreSchedulerWeekday = int.TryParse(Get("ADVISOR_SCORE_SCHEDULER_WEEKDAY", "0"), out var wd) ? Math.Clamp(wd, 0, 6) : 0,
            CorsOrigins = corsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            RateLimitPerMinute = int.TryParse(Get("RATE_LIMIT_PER_MINUTE"), out var rpm) && rpm >= 0 ? rpm : 100,
            AdvisorSyncCooldownMinutes = int.TryParse(Get("ADVISOR_SYNC_COOLDOWN_MINUTES"), out var acm) && acm >= 0 ? acm : 15,
            OperationCooldownMinutes = int.TryParse(Get("OPERATION_COOLDOWN_MINUTES"), out var ocm) && ocm >= 0 ? ocm : 10,
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
