using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.AlertCatalog;
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
builder.Services.AddScoped<IUserDirectory, SqlUserDirectory>();
builder.Services.AddScoped<IAlertCatalogStore, SqlAlertCatalogStore>();
builder.Services.AddBitJwtAuth(config);

// Motor de costos – capa de precios (Fase 1). Registros aditivos: aun no hay rutas de
// costos que los consuman, pero deben resolver vía DI.
//   - PricingConstants: tablas/umbrales estaticos, sin estado -> singleton.
//   - SqlPriceCache: usa ISqlConnectionFactory (ya registrado) -> scoped.
//   - RetailPriceClient: usa HttpClient inyectado por IHttpClientFactory (AddHttpClient).
//   - SqlPriceRepository: orquesta cache + cliente + constantes -> scoped.
builder.Services.AddSingleton<IPricingConstants, PricingConstants>();
builder.Services.AddScoped<IPriceCache, SqlPriceCache>();
builder.Services.AddHttpClient<IRetailPriceClient, RetailPriceClient>();
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
