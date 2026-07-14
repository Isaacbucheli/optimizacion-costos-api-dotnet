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
var config = AppConfig.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(config);

// JSON en snake_case para mantener el mismo contrato que el FastAPI (alert_number, is_active, ...).
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

// Datos y auth
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();

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
// Optimización / barrido de tenant (B6): 7 checks KQL + estimación de ahorro + hallazgos/estado.
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Optimization.ICostEstimation, OptimizacionCostos.Api.Features.Optimization.CostEstimation>();
// Right-sizing (Fase 2.5, Grupo B): reusa IReportMetrics (Azure Monitor). Requiere Monitoring Reader.
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Optimization.IRightSizingAnalyzer, OptimizacionCostos.Api.Features.Optimization.RightSizingAnalyzer>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Optimization.IOptimizationService, OptimizacionCostos.Api.Features.Optimization.OptimizationService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Optimization.IOptimizationExcelExporter, OptimizacionCostos.Api.Features.Optimization.OptimizationExcelExporter>();

// Módulo WAF (B7). Schema lazy en cada store. Reusa IChatCompletionClient, IAzureCredentialFactory,
// IAnalysisAccess, ICostResultsQuery, ISqlConnectionFactory ya registrados.
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafCatalogStore, OptimizacionCostos.Api.Features.Waf.SqlWafCatalogStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafRecommendationStore, OptimizacionCostos.Api.Features.Waf.SqlWafRecommendationStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafTrackingStore, OptimizacionCostos.Api.Features.Waf.SqlWafTrackingStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafIngestionStore, OptimizacionCostos.Api.Features.Waf.SqlWafIngestionStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafDedupService, OptimizacionCostos.Api.Features.Waf.WafDedupService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafCuratorService, OptimizacionCostos.Api.Features.Waf.WafCuratorService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafConsolidationService, OptimizacionCostos.Api.Features.Waf.WafConsolidationService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafDedupResolverFactory, OptimizacionCostos.Api.Features.Waf.WafDedupResolverFactory>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IAdvisorApiClient, OptimizacionCostos.Api.Features.Waf.AdvisorApiClient>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IAdvisorScoreStore, OptimizacionCostos.Api.Features.Waf.SqlAdvisorScoreStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IAdvisorScoreService, OptimizacionCostos.Api.Features.Waf.AdvisorScoreService>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafExcelExporter, OptimizacionCostos.Api.Features.Waf.ClosedXmlWafExporter>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafExcelImporter, OptimizacionCostos.Api.Features.Waf.ClosedXmlWafImporter>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Waf.IWafSyncOrchestrator, OptimizacionCostos.Api.Features.Waf.WafSyncOrchestrator>();
builder.Services.AddHostedService<OptimizacionCostos.Api.Features.Waf.AdvisorScoreScheduledService>();
builder.Services.AddScoped<IUserDirectory, SqlUserDirectory>();
builder.Services.AddScoped<IAlertCatalogStore, SqlAlertCatalogStore>();
builder.Services.AddScoped<OptimizacionCostos.Api.Features.PolicyCatalog.IPolicyCatalogStore, OptimizacionCostos.Api.Features.PolicyCatalog.SqlPolicyCatalogStore>();
// Asignación de consultores (Gestión CDC): personas + asignaciones N:N + reasignación masiva.
// Schema lazy SIN seed (datos sensibles de clientes reales; repos públicos).
builder.Services.AddScoped<OptimizacionCostos.Api.Features.Consultants.IConsultantsStore, OptimizacionCostos.Api.Features.Consultants.SqlConsultantsStore>();
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

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors(CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Necesario para WebApplicationFactory<Program> en los tests.
public partial class Program;
