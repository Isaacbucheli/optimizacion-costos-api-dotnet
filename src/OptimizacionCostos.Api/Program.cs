using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.AlertCatalog;
using OptimizacionCostos.Api.Features.AzureIntegration;
using OptimizacionCostos.Api.Features.Identity;
using OptimizacionCostos.Api.Features.CostEngine.Api;
using OptimizacionCostos.Api.Features.CostEngine.Engine;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using OptimizacionCostos.Api.Features.CostEngine.Scenarios;

var builder = WebApplication.CreateBuilder(args);

// Kestrel anuncia "Server: Kestrel" en cada respuesta. Es divulgación de tecnología
// (ZAP 10036) y no le sirve de nada al cliente.
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

var config = AppConfig.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(config);

// JSON en snake_case para mantener el mismo contrato que el FastAPI (alert_number, is_active, ...).
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    // Todo timestamp sale como UTC con 'Z'; sin esto el front interpreta la hora UTC como local.
    o.JsonSerializerOptions.Converters.Add(new UtcDateTimeJsonConverter());
});

// Datos y auth
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
// Conexión a la BD del tablero de pendientes (Seguimiento CDC): otro servidor, otro dueño del
// esquema. Si faltan las SQL_*2 la factory queda "no configurada" y solo ese módulo responde 503.
builder.Services.AddSingleton<ISeguimientoSqlConnectionFactory, SeguimientoSqlConnectionFactory>();

// Capa de integración Azure (B1, fundamento de import/credenciales/suscripciones/optimización/CDC):
//   - KeyVaultService: guarda/lee/borra client_secret en Key Vault (DefaultAzureCredential).
//   - SqlAzureCredentialFactory: arma ClientSecretCredential desde SQL+KV (el secreto nunca sale).
//   - ResourceGraphRunner: ejecuta KQL del catálogo contra Resource Graph con paginación.
// Aditivos: aún no hay rutas que los consuman; resuelven vía DI para las fases siguientes.
builder.Services.AddSingleton<IKeyVaultService, KeyVaultService>();
// Sesiones Azure con cuenta de usuario (Lighthouse, clientes temporales). Singleton en memoria;
// el token nunca se persiste. Gateado por USER_SESSION_AUTH_ENABLED en los endpoints.
builder.Services.AddSingleton<OptimizacionCostos.Api.Features.AzureIntegration.UserSessions.IDeviceCodeFlow,
    OptimizacionCostos.Api.Features.AzureIntegration.UserSessions.AzureDeviceCodeFlow>();
builder.Services.AddSingleton<OptimizacionCostos.Api.Features.AzureIntegration.UserSessions.IAzureUserSessionService,
    OptimizacionCostos.Api.Features.AzureIntegration.UserSessions.AzureUserSessionService>();
// Catálogo Lighthouse (clientes agrupados por tenant) para la sesión del usuario. Cachea 10 min.
builder.Services.AddSingleton<OptimizacionCostos.Api.Features.AzureIntegration.UserSessions.ILighthouseCatalogService,
    OptimizacionCostos.Api.Features.AzureIntegration.UserSessions.LighthouseCatalogService>();
builder.Services.AddScoped<IAzureCredentialFactory, SqlAzureCredentialFactory>();
builder.Services.AddSingleton<IResourceGraphRunner, ResourceGraphRunner>();
// Credenciales + suscripciones por cliente (B3): CRUD + sync ARM. Usan B1 (KV + factory).
builder.Services.AddScoped<IClientCredentialStore, SqlClientCredentialStore>();
builder.Services.AddScoped<IClientSubscriptionStore, SqlClientSubscriptionStore>();
builder.Services.AddScoped<ISubscriptionSyncService, SubscriptionSyncService>();
// Import de inventario / Resource Graph (B4): usa catálogo + B1 (RG + credenciales) + B3 (sync).
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Inventory.IInventoryImportService, OptimizacionCostos.Api.Features.Inventory.InventoryImportService>();
// Enriquecimiento ARM del servicio storage_files (fileshares, corte de 10 TiB).
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Inventory.IStorageFilesEnricher, OptimizacionCostos.Api.Features.Inventory.StorageFilesEnricher>();
// CRUD admin del catálogo de servicios (B4): gestión + sugerencia IA desde discovery.
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Catalog.IServiceCatalogAdmin, OptimizacionCostos.Api.Features.Catalog.SqlServiceCatalogAdmin>();
// Gestión CDC (B5): reservas (ARM/Capacity/Consumption), cobertura RI, power history (Activity Log).
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Cdc.IAzureReservationsClient, OptimizacionCostos.Api.Features.Cdc.AzureReservationsClient>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Cdc.IReservationService, OptimizacionCostos.Api.Features.Cdc.ReservationService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Cdc.IRiCoverageService, OptimizacionCostos.Api.Features.Cdc.RiCoverageService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Cdc.IPowerHistoryService, OptimizacionCostos.Api.Features.Cdc.PowerHistoryService>();
// B5 background: refresh de encendido/apagado como job (202 + polling), calcado de ReportGeneration.
builder.Services.AddSingleton<OptimizacionCostos.Api.Features.Cdc.IPowerHistoryJobQueue, OptimizacionCostos.Api.Features.Cdc.PowerHistoryJobQueue>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Cdc.IPowerHistoryJobStore, OptimizacionCostos.Api.Features.Cdc.SqlPowerHistoryJobStore>();
builder.Services.AddHostedService<OptimizacionCostos.Api.Features.Cdc.PowerHistoryBackgroundService>();
// Revisión de accesos (Gestión CDC): RBAC (ARM) + Entra ID (Graph) por cliente. Sync como job
// en background (202 + polling), calcado de PowerHistory.
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Cdc.AccessReview.IAccessReviewStore,
    OptimizacionCostos.Api.Features.Cdc.AccessReview.SqlAccessReviewStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Cdc.AccessReview.IAccessReviewDecisionStore,
    OptimizacionCostos.Api.Features.Cdc.AccessReview.SqlAccessReviewDecisionStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Cdc.AccessReview.IAccessReviewGraphClient,
    OptimizacionCostos.Api.Features.Cdc.AccessReview.AccessReviewGraphClient>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Cdc.AccessReview.IAccessReviewArmClient,
    OptimizacionCostos.Api.Features.Cdc.AccessReview.AccessReviewArmClient>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Cdc.AccessReview.IAccessReviewSyncService,
    OptimizacionCostos.Api.Features.Cdc.AccessReview.AccessReviewSyncService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Cdc.AccessReview.IAccessReviewExcelExporter,
    OptimizacionCostos.Api.Features.Cdc.AccessReview.AccessReviewExcelExporter>();
builder.Services.AddSingleton<OptimizacionCostos.Api.Features.Cdc.AccessReview.IAccessReviewJobQueue,
    OptimizacionCostos.Api.Features.Cdc.AccessReview.AccessReviewJobQueue>();
builder.Services.AddHostedService<OptimizacionCostos.Api.Features.Cdc.AccessReview.AccessReviewBackgroundService>();
// Optimización / barrido de tenant (B6): 7 checks KQL + estimación de ahorro + hallazgos/estado.
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Optimization.ICostEstimation, OptimizacionCostos.Api.Features.Optimization.CostEstimation>();
// Right-sizing (Fase 2.5, Grupo B): reusa IReportMetrics (Azure Monitor). Requiere Monitoring Reader.
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Optimization.IRightSizingAnalyzer, OptimizacionCostos.Api.Features.Optimization.RightSizingAnalyzer>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Optimization.IOptimizationService, OptimizacionCostos.Api.Features.Optimization.OptimizationService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Optimization.IOptimizationExcelExporter, OptimizacionCostos.Api.Features.Optimization.OptimizationExcelExporter>();

// Boletín Azure (Fase 1): retiros/deprecaciones vía Advisor + Service Health, sin costos.
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Boletin.IBoletinService,
    OptimizacionCostos.Api.Features.Boletin.BoletinService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Boletin.IBoletinTranslationService,
    OptimizacionCostos.Api.Features.Boletin.BoletinTranslationService>();
// Runtimes de apps Windows (Task 6): el runtime no está en Resource Graph, se resuelve por ARM.
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Boletin.ISiteRuntimeArmClient,
    OptimizacionCostos.Api.Features.Boletin.SiteRuntimeArmClient>();
// Catálogo de lifecycle (Fase 2 Entrega 2): fin de soporte de SO/BD, global (no por cliente). Schema+seed lazy.
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Boletin.IBoletinLifecycleStore,
    OptimizacionCostos.Api.Features.Boletin.BoletinLifecycleStore>();

// Módulo WAF (B7). Schema lazy en cada store. Reusa IChatCompletionClient, IAzureCredentialFactory,
// IAnalysisAccess, ICostResultsQuery, ISqlConnectionFactory ya registrados.
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafCatalogStore, OptimizacionCostos.Api.Features.Waf.SqlWafCatalogStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafRecommendationStore, OptimizacionCostos.Api.Features.Waf.SqlWafRecommendationStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafTrackingStore, OptimizacionCostos.Api.Features.Waf.SqlWafTrackingStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafIngestionStore, OptimizacionCostos.Api.Features.Waf.SqlWafIngestionStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafDedupService, OptimizacionCostos.Api.Features.Waf.WafDedupService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafCuratorService, OptimizacionCostos.Api.Features.Waf.WafCuratorService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafTranslationService, OptimizacionCostos.Api.Features.Waf.WafTranslationService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafConsolidationService, OptimizacionCostos.Api.Features.Waf.WafConsolidationService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafDedupResolverFactory, OptimizacionCostos.Api.Features.Waf.WafDedupResolverFactory>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IAdvisorApiClient, OptimizacionCostos.Api.Features.Waf.AdvisorApiClient>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IAdvisorScoreStore, OptimizacionCostos.Api.Features.Waf.SqlAdvisorScoreStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IAdvisorScoreService, OptimizacionCostos.Api.Features.Waf.AdvisorScoreService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafExcelExporter, OptimizacionCostos.Api.Features.Waf.ClosedXmlWafExporter>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafExcelImporter, OptimizacionCostos.Api.Features.Waf.ClosedXmlWafImporter>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafSyncOrchestrator, OptimizacionCostos.Api.Features.Waf.WafSyncOrchestrator>();
builder.Services.AddSingleton<OptimizacionCostos.Api.Features.Waf.IWafAdvisorSyncJobQueue, OptimizacionCostos.Api.Features.Waf.WafAdvisorSyncJobQueue>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafAdvisorSyncJobStore, OptimizacionCostos.Api.Features.Waf.SqlWafAdvisorSyncJobStore>();
builder.Services.AddHostedService<OptimizacionCostos.Api.Features.Waf.WafAdvisorSyncBackgroundService>();
builder.Services.AddHostedService<OptimizacionCostos.Api.Features.Waf.AdvisorScoreScheduledService>();
builder.Services.AddScoped<IUserDirectory, SqlUserDirectory>();
builder.Services.AddScoped<IAlertCatalogStore, SqlAlertCatalogStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.PolicyCatalog.IPolicyCatalogStore, OptimizacionCostos.Api.Features.PolicyCatalog.SqlPolicyCatalogStore>();
// Asignación de consultores (Gestión CDC): personas + asignaciones N:N + reasignación masiva.
// Schema lazy SIN seed (datos sensibles de clientes reales; repos públicos).
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Consultants.IConsultantsStore, OptimizacionCostos.Api.Features.Consultants.SqlConsultantsStore>();
// Pendientes y bloqueantes (Gestión CDC): usa la BD del tablero, NO la de la plataforma.
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Pendientes.IPendientesStore, OptimizacionCostos.Api.Features.Pendientes.SqlPendientesStore>();
builder.Services.AddBitJwtAuth(config);

// Identidad: emisión de JWT (login/bootstrap) + administración de usuarios y asignaciones (B3).
builder.Services.AddSingleton<TokenIssuer>();
builder.Services.AddScoped<IAppUserStore, SqlAppUserStore>();

// Matriz de permisos rol×módulo (lector/consultor): store SQL + decisión cacheada.
// AddMemoryCache ya está registrado más abajo (bloque FinOps); el orden no importa.
builder.Services.AddScoped<IModulePermissionStore, SqlModulePermissionStore>();
builder.Services.AddScoped<IModulePermissionService, ModulePermissionService>();

// Blob Storage (B3 logos; reusado por B6/B8) + administración de clientes (B3).
builder.Services.AddSingleton<OptimizacionCostos.Api.Features.Storage.IBlobStorageService, OptimizacionCostos.Api.Features.Storage.BlobStorageService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Clients.IClientStore, OptimizacionCostos.Api.Features.Clients.SqlClientStore>();

// Informes mensuales + export Excel/Word + carga de archivos (B8). Reusa B1 (credenciales + RG),
// Storage, IChatCompletionClient (narrativa) e IAdvisorScoreStore (historial). El JSON del informe
// vive en Blob; SQL solo guarda el índice + el plan de acción editable.
//   - IReportJobQueue (singleton, Channel) + ReportGenerationBackgroundService reemplazan
//     FastAPI BackgroundTasks: el POST .../generate responde 202 y encola; el worker drena la cola.
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Reports.IReportStore, OptimizacionCostos.Api.Features.Reports.SqlReportStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Reports.IReportInventory, OptimizacionCostos.Api.Features.Reports.ReportInventory>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Reports.IReportMetrics, OptimizacionCostos.Api.Features.Reports.ReportMetrics>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Reports.IReportAlerts, OptimizacionCostos.Api.Features.Reports.ReportAlerts>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Reports.IReportNarrativeService, OptimizacionCostos.Api.Features.Reports.ReportNarrativeService>();
builder.Services.AddSingleton<OptimizacionCostos.Api.Features.Reports.IReportExecutive, OptimizacionCostos.Api.Features.Reports.ReportExecutive>();
builder.Services.AddSingleton<OptimizacionCostos.Api.Features.Reports.IReportWordExporter, OptimizacionCostos.Api.Features.Reports.ReportWordExporter>();
// dbo.analysis_files para ExcelController (generate + download), extraído del controller para
// poder testearlo con WebApplicationFactory + fake (sin BD real).
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Reports.IAnalysisFileStore, OptimizacionCostos.Api.Features.Reports.SqlAnalysisFileStore>();
// Excel v3 (código, único motor): data source con las queries de costos + escenarios de
// ICostResultsQuery (ya registrado por CostEngine, más abajo).
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Reports.ExcelV3.ICostExcelDataSourceV3, OptimizacionCostos.Api.Features.Reports.ExcelV3.CostExcelDataSourceV3>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Reports.ExcelV3.ICostExcelExporterV3, OptimizacionCostos.Api.Features.Reports.ExcelV3.CodeCostExcelExporter>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Reports.IReportBuilder, OptimizacionCostos.Api.Features.Reports.ReportBuilder>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Reports.IReportGenerationService, OptimizacionCostos.Api.Features.Reports.ReportGenerationService>();
builder.Services.AddSingleton<OptimizacionCostos.Api.Features.Reports.IReportJobQueue, OptimizacionCostos.Api.Features.Reports.ReportJobQueue>();
builder.Services.AddHostedService<OptimizacionCostos.Api.Features.Reports.ReportGenerationBackgroundService>();

// Motor de costos – capa de precios (Fase 1). Registros aditivos: aun no hay rutas de
// costos que los consuman, pero deben resolver vía DI.
//   - PricingConstants: tablas/umbrales estaticos, sin estado -> singleton.
//   - SqlPriceCache: usa ISqlConnectionFactory (ya registrado) -> scoped.
//   - RetailPriceClient: usa HttpClient inyectado por IHttpClientFactory (AddHttpClient).
//   - SqlPriceRepository: orquesta cache + cliente + constantes -> scoped.
builder.Services.AddSingleton<IPricingConstants, PricingConstants>();
builder.Services.AddScoped<IPriceCache, SqlPriceCache>();
builder.Services.AddHttpClient<IRetailPriceClient, RetailPriceClient>();
// Asistente de precios IA (fallback cuando la selección determinista no halla precio):
//   - AzureOpenAiChatClient: HTTP a Azure OpenAI (timeout 45s) vía IHttpClientFactory.
//   - SqlPriceAssistantAudit: persiste cada decisión en dbo.price_assistant_audit.
//   - SqlPriceAssistant: elige solo de candidatos REALES, conf>=0.6, marca "IA asistida".
// SqlPriceRepository lo recibe como dependencia OPCIONAL del ctor.
builder.Services.AddHttpClient<IChatCompletionClient, AzureOpenAiChatClient>(c => c.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddScoped<IPriceAssistantAudit, SqlPriceAssistantAudit>();
builder.Services.AddScoped<IPriceAssistant, SqlPriceAssistant>();
builder.Services.AddScoped<IPriceRepository, SqlPriceRepository>();

// Motor de costos – orquestación y escenarios (Fase 2). Aditivos: sin rutas que los usen aún.
builder.Services.AddScoped<IServiceCatalog, SqlServiceCatalog>();
builder.Services.AddScoped<IResourceLoader, SqlResourceLoader>();
builder.Services.AddScoped<ICostResultStore, SqlCostResultStore>();
builder.Services.AddScoped<CostEngine>();
builder.Services.AddScoped<IScenarioDataSource, SqlScenarioDataSource>();
builder.Services.AddScoped<ScenarioService>();

// Motor de costos – endpoints REST (Fase 3): control de acceso por cliente + consultas
// del router. Port de cost_calculation.py + access_control.py.
builder.Services.AddScoped<IAnalysisAccess, SqlAnalysisAccess>();
builder.Services.AddScoped<ICostResultsQuery, SqlCostResultsQuery>();

// FinOps Toolkit fase 1: datasets de referencia (open data, MIT) + elegibilidad RI.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.FinOpsData.IFinOpsDataStore, OptimizacionCostos.Api.Features.FinOpsData.SqlFinOpsDataStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.FinOpsData.IFinOpsRefData, OptimizacionCostos.Api.Features.FinOpsData.SqlFinOpsRefData>();
builder.Services.AddHttpClient<OptimizacionCostos.Api.Features.FinOpsData.IFinOpsDataRefreshService, OptimizacionCostos.Api.Features.FinOpsData.FinOpsDataRefreshService>(c => c.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddScoped<OptimizacionCostos.Api.Features.FinOpsData.IRiEligibilityEnricher, OptimizacionCostos.Api.Features.FinOpsData.RiEligibilityEnricher>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.FinOpsData.IRiDiagnosticsQuery, OptimizacionCostos.Api.Features.FinOpsData.SqlRiDiagnosticsQuery>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.FinOpsData.ICoverageQuery, OptimizacionCostos.Api.Features.FinOpsData.SqlCoverageQuery>();

// CORS explicito (paridad con CORS_ORIGINS del FastAPI)
const string CorsPolicy = "BitCors";
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p =>
{
    if (config.CorsOrigins.Length > 0)
        p.WithOrigins(config.CorsOrigins).AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Optimizacion Costos API (.NET PoC)", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Pega el JWT del login (mismo token del FastAPI).",
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
    });
});

var app = builder.Build();

// Swagger publicaba toda la superficie de la API (155 KB de contrato) a cualquiera sin token.
// Ahora queda cerrado salvo en Development o si se prende SWAGGER_ENABLED a mano. Para
// comprobar que un deploy quedó vivo usar /health, NO swagger.json.
var swaggerEnabled = app.Environment.IsDevelopment() || config.SwaggerEnabled;

// Cabeceras de seguridad de la API. Va PRIMERO en el pipeline para que salgan también en
// las respuestas de error (401/403/404/500), que es donde los escáneres suelen encontrarlas
// ausentes. Se revisan en cualquier respuesta HTTP, no solo en HTML.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Cache-Control"] = "no-store";
    // HSTS a mano en vez de UseHsts(): detrás del proxy de App Service la petición llega a
    // Kestrel como HTTP, así que Request.IsHttps es false y UseHsts no la emitiría nunca.
    // Emitirla siempre es seguro porque el navegador ignora la cabecera si no la recibió
    // sobre TLS, y el TLS lo termina el front end de App Service.
    headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    // La API solo devuelve datos: no hay nada que cargar. La única excepción es Swagger, que es
    // HTML+JS+CSS y con esta CSP se vería en blanco; la excepción existe solo mientras Swagger
    // esté habilitado, así que en producción /swagger es un 404 con la CSP estricta puesta.
    if (!swaggerEnabled || !context.Request.Path.StartsWithSegments("/swagger"))
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
    await next();
});

if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Red de seguridad (R1): sin CORS_ORIGINS en un entorno no-dev, la política CORS no agrega ningún
// origen y el navegador bloquea TODAS las llamadas del front (SWA) con "Failed to fetch", aunque la
// API responda perfecto por curl. ASP.NET no lanza excepción por esto, así que sin este aviso el
// fallo es invisible en los logs del App Service. En desarrollo el front usa el proxy "/api" y no
// necesita CORS, por eso solo avisamos fuera de Development. Ver STACK-NUEVO.md.
if (!app.Environment.IsDevelopment() && config.CorsOrigins.Length == 0)
{
    app.Logger.LogError(
        "CORS_ORIGINS está vacío en el entorno '{Environment}': la política CORS no permite ningún origen, " +
        "por lo que el navegador bloqueará las llamadas del front con 'Failed to fetch'. Configura CORS_ORIGINS " +
        "con el origen del Static Web App en la configuración del App Service.",
        app.Environment.EnvironmentName);
}

app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Auto-reparación de superadmins al arrancar: fuerza rol=admin + activo para los correos
// protegidos (SUPERADMIN_EMAILS). Best-effort: si la BD no está lista, el login del superadmin
// reconcilia igual. No hace nada si la lista está vacía (default seguro).
if (config.SuperAdminEmails.Length > 0)
{
    try
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppUserStore>()
            .EnsureSuperAdminsAsync(config.SuperAdminEmails);
    }
    catch { /* la BD puede no responder en el arranque; se reintenta en el próximo login del superadmin */ }
}

// Seed idempotente del catálogo de servicios (snapshots + storage_files, spec 2026-07-24).
// Best-effort: si la BD no responde en el arranque, se reintenta en el próximo arranque.
try
{
    using var scope = app.Services.CreateScope();
    await OptimizacionCostos.Api.Features.Catalog.CatalogSeed.EnsureAsync(
        scope.ServiceProvider.GetRequiredService<OptimizacionCostos.Api.Features.Catalog.IServiceCatalogAdmin>(),
        app.Logger);
}
catch (Exception ex)
{
    // BD no disponible al arrancar (u otro fallo puntual); el seed es idempotente y se reintenta
    // en el próximo arranque, pero si nunca corre el catálogo queda sin las filas nuevas sin
    // dejar rastro. Se registra para poder diagnosticarlo.
    app.Logger.LogWarning(ex, "CatalogSeed.EnsureAsync falló al arrancar; se reintentará en el próximo arranque.");
}

app.Run();

// Necesario para WebApplicationFactory<Program> en los tests.
public partial class Program;
