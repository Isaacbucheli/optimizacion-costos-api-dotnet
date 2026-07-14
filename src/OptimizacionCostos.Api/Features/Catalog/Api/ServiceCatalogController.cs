using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OptimizacionCostos.Api.Auth;
using OptimizacionCostos.Api.Configuration;
using OptimizacionCostos.Api.Features.CostEngine.Pricing;
using OptimizacionCostos.Api.Features.Inventory;

namespace OptimizacionCostos.Api.Features.Catalog.Api;

/// <summary>
/// CRUD del catálogo de servicios + sugerencia IA desde discovery. Port de
/// app/routes/service_catalog.py (prefix /service-catalog).
/// </summary>
[ApiController]
[Authorize]
[Route("service-catalog")]
public sealed partial class ServiceCatalogController(
    IServiceCatalogAdmin store,
    IChatCompletionClient chat,
    AppConfig config) : ControllerBase
{
    public sealed record ServiceCreateRequest(
        string? ServiceKey, string? DisplayName, string? AzureResourceType, string? ServiceCategory,
        string? DetailTableName, string? InserterKey, string? CalculatorKey, string? KqlQuery,
        bool RiApplicable = false, string? RiFilterField = null, string? RiFilterValues = null, string? RiExcludeValues = null,
        bool AhbApplicable = false, bool RequiresManualCost = false, string? ExcelSheetName = null,
        int DisplayOrder = 100, bool IsActive = true, string? Notes = null);

    public sealed record ServiceUpdateRequest(
        string? DisplayName, string? AzureResourceType, string? ServiceCategory, string? DetailTableName,
        string? InserterKey, string? CalculatorKey, string? KqlQuery, bool? RiApplicable,
        string? RiFilterField, string? RiFilterValues, string? RiExcludeValues, bool? AhbApplicable,
        bool? RequiresManualCost, string? ExcelSheetName, int? DisplayOrder, bool? IsActive, string? Notes);

    [HttpGet]
    [RequireModule(Modules.ServiceCatalog)]
    public async Task<IActionResult> ListAll(CancellationToken ct) => Ok(await store.ListAsync(false, false, ct));

    [HttpGet("active")]
    public async Task<IActionResult> ListActive(CancellationToken ct) => Ok(await store.ListAsync(true, false, ct));

    [HttpGet("inserter-keys")]
    [RequireModule(Modules.ServiceCatalog)]
    public IActionResult InserterKeys() => Ok(InventoryInserter.InserterKeys.OrderBy(k => k).ToList());

    [HttpPost("suggest-from-discovery/{discoveryId:int}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> SuggestFromDiscovery(int discoveryId, CancellationToken ct)
    {
        var disc = await store.GetDiscoveryAsync(discoveryId, ct);
        if (disc is null) return NotFound(new { detail = "Discovery row not found" });

        Dictionary<string, object?> suggestion;
        try { suggestion = await BuildSuggestionAsync(disc, ct); }
        catch (Exception ex)
        {
            suggestion = FallbackSuggestion(disc);
            suggestion["reason_es"] = $"No se pudo consultar IA ({ex.GetType().Name}); se genero una sugerencia base.";
        }

        await store.InsertAiSuggestionAuditAsync(
            discoveryId, disc.ResourceType, JsonSerializer.Serialize(suggestion), suggestion.GetValueOrDefault("reason_es") as string, ct);
        return Ok(suggestion);
    }

    [HttpGet("{serviceKey}")]
    [RequireModule(Modules.ServiceCatalog)]
    public async Task<IActionResult> GetOne(string serviceKey, CancellationToken ct)
    {
        var svc = await store.GetAsync(serviceKey, ct);
        return svc is null ? NotFound(new { detail = "Service not found" }) : Ok(svc);
    }

    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Create([FromBody] ServiceCreateRequest p, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.ServiceKey) || string.IsNullOrWhiteSpace(p.DisplayName)
            || string.IsNullOrWhiteSpace(p.AzureResourceType) || string.IsNullOrWhiteSpace(p.ServiceCategory)
            || string.IsNullOrWhiteSpace(p.InserterKey) || string.IsNullOrWhiteSpace(p.CalculatorKey) || string.IsNullOrWhiteSpace(p.KqlQuery))
            return BadRequest(new { detail = "Faltan campos requeridos" });
        if (await store.ExistsAsync(p.ServiceKey!, ct))
            return Conflict(new { detail = "service_key already exists" });
        if (!ValidInserterKey(p.InserterKey!, out var err))
            return BadRequest(new { detail = err });

        await store.CreateAsync(new CatalogEntryWrite(
            p.ServiceKey!, p.DisplayName!, p.AzureResourceType!, p.ServiceCategory!, p.DetailTableName,
            p.InserterKey!, p.CalculatorKey!, p.KqlQuery!, p.RiApplicable, p.RiFilterField, p.RiFilterValues,
            p.RiExcludeValues, p.AhbApplicable, p.RequiresManualCost, p.ExcelSheetName, p.DisplayOrder, p.IsActive, p.Notes), ct);
        return Ok(new { message = "Servicio creado", service_key = p.ServiceKey });
    }

    [HttpPut("{serviceKey}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Update(string serviceKey, [FromBody] ServiceUpdateRequest p, CancellationToken ct)
    {
        if (p.InserterKey is not null && !ValidInserterKey(p.InserterKey, out var err))
            return BadRequest(new { detail = err });

        var sets = new List<(string, object?)>();
        void Add(string col, object? val) { if (val is not null) sets.Add((col, val)); }
        Add("display_name", p.DisplayName); Add("azure_resource_type", p.AzureResourceType);
        Add("service_category", p.ServiceCategory); Add("detail_table_name", p.DetailTableName);
        Add("inserter_key", p.InserterKey); Add("calculator_key", p.CalculatorKey); Add("kql_query", p.KqlQuery);
        if (p.RiApplicable is not null) sets.Add(("ri_applicable", p.RiApplicable.Value ? 1 : 0));
        Add("ri_filter_field", p.RiFilterField); Add("ri_filter_values", p.RiFilterValues); Add("ri_exclude_values", p.RiExcludeValues);
        if (p.AhbApplicable is not null) sets.Add(("ahb_applicable", p.AhbApplicable.Value ? 1 : 0));
        if (p.RequiresManualCost is not null) sets.Add(("requires_manual_cost", p.RequiresManualCost.Value ? 1 : 0));
        Add("excel_sheet_name", p.ExcelSheetName);
        if (p.DisplayOrder is not null) sets.Add(("display_order", p.DisplayOrder.Value));
        if (p.IsActive is not null) sets.Add(("is_active", p.IsActive.Value ? 1 : 0));
        Add("notes", p.Notes);

        if (sets.Count == 0) return BadRequest(new { detail = "No fields to update" });
        var ok = await store.UpdateAsync(serviceKey, sets, ct);
        return ok ? Ok(new { message = "Servicio actualizado", service_key = serviceKey }) : NotFound(new { detail = "Service not found" });
    }

    [HttpDelete("{serviceKey}")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> SoftDelete(string serviceKey, CancellationToken ct)
    {
        var ok = await store.SoftDeleteAsync(serviceKey, ct);
        return ok ? Ok(new { message = "Servicio desactivado", service_key = serviceKey }) : NotFound(new { detail = "Service not found" });
    }

    // -------------------- suggestion helpers (port de _fallback_catalog_suggestion / _call_catalog_ai) --------------------

    private bool ValidInserterKey(string key, out string error)
    {
        if (InventoryInserter.InserterKeys.Contains(key)) { error = ""; return true; }
        error = $"inserter_key '{key}' no está registrado en el código. Registrados: [{string.Join(", ", InventoryInserter.InserterKeys.OrderBy(k => k))}].";
        return false;
    }

    private static string ServiceKeyFromType(string rt)
    {
        var leaf = (rt ?? "").Split('/').LastOrDefault() ?? "";
        var key = NonAlnum().Replace(leaf.ToLowerInvariant(), "_").Trim('_');
        return key.Length > 50 ? key[..50] : (key.Length == 0 ? "azure_service" : key);
    }

    private static string DisplayNameFromType(string rt)
    {
        var leaf = (rt ?? "").Split('/').LastOrDefault() ?? "";
        var spaced = CamelBoundary().Replace(leaf, "$1 $2").Replace("-", " ").Replace("_", " ");
        var words = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        var name = string.Join(" ", words);
        return string.IsNullOrEmpty(name) ? rt ?? "" : name;
    }

    private static string CategoryFromType(string rt)
    {
        var t = (rt ?? "").ToLowerInvariant();
        if (t.Contains(".sql/") || t.Contains("database") || t.Contains("db")) return "database";
        if (t.Contains(".network/")) return "network";
        if (t.Contains(".compute/")) return "compute";
        if (t.Contains(".storage/")) return "storage";
        if (t.Contains("cache") || t.Contains("redis")) return "cache";
        if (t.Contains("keyvault") || t.Contains("security")) return "security";
        return "other";
    }

    private static Dictionary<string, object?> FallbackSuggestion(DiscoveryForSuggest d)
    {
        var rt = d.ResourceType;
        var display = DisplayNameFromType(rt);
        return new Dictionary<string, object?>
        {
            ["service_key"] = ServiceKeyFromType(rt),
            ["display_name"] = display,
            ["azure_resource_type"] = rt,
            ["service_category"] = CategoryFromType(rt),
            ["detail_table_name"] = null,
            ["inserter_key"] = "",
            ["calculator_key"] = "",
            ["kql_query"] = $"Resources\n| where type =~ '{rt}'\n| project id, subscriptionId, resourceGroup, name, type, location, skuName=tostring(sku.name), properties",
            ["ri_applicable"] = false,
            ["ahb_applicable"] = false,
            ["requires_manual_cost"] = true,
            ["excel_sheet_name"] = display.Length > 31 ? display[..31] : display,
            ["display_order"] = 100,
            ["notes"] = "Sugerencia base. Revisar si requiere inserter/calculator antes de activar costos.",
            ["reason_es"] = "Sugerencia heuristica generada sin IA disponible.",
        };
    }

    private async Task<Dictionary<string, object?>> BuildSuggestionAsync(DiscoveryForSuggest d, CancellationToken ct)
    {
        var fallback = FallbackSuggestion(d);
        if (!(config.AzureOpenAiEnabled && !string.IsNullOrWhiteSpace(config.AzureOpenAiEndpoint)
              && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiKey) && !string.IsNullOrWhiteSpace(config.AzureOpenAiDeployment)))
            return fallback;

        var existing = (await store.ListAsync(false, false, ct))
            .Take(80)
            .Select(s => new Dictionary<string, object?>
            {
                ["service_key"] = s["service_key"], ["display_name"] = s["display_name"],
                ["azure_resource_type"] = s["azure_resource_type"], ["service_category"] = s["service_category"],
            }).ToList();

        const string system =
            "Eres un asistente de catalogo Azure. Sugieres metadata tecnica revisable para una plataforma "
            + "de optimizacion de costos. No creas servicios ni inventas costos. Responde solo JSON valido.";
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["resource_type"] = d.ResourceType,
            ["resource_count"] = d.ResourceCount,
            ["sample_names"] = d.SampleNames,
            ["existing_catalog"] = existing,
            ["rules"] = new[]
            {
                "No inventes precios ni costos.", "La sugerencia no se creara automaticamente.",
                "Si el recurso suele no tener costo directo, marca requires_manual_cost true.",
                "Usa service_key corto en snake_case y maximo 50 caracteres.", "Devuelve solo JSON valido.",
            },
        });

        var raw = chat.Complete(system, payload);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var t = raw.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            t = t.Trim('`').Trim();
            if (t.StartsWith("json", StringComparison.OrdinalIgnoreCase)) t = t[4..].Trim();
        }
        using var doc = JsonDocument.Parse(t);
        var root = doc.RootElement;
        // Mezcla: arranca del fallback y sobrescribe con lo que devuelva la IA.
        foreach (var key in fallback.Keys.ToList())
            if (root.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null)
                fallback[key] = v.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => v.TryGetInt32(out var n) ? n : v.GetDouble(),
                    _ => v.ToString(),
                };
        fallback["azure_resource_type"] = d.ResourceType; // siempre el real
        return fallback;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnum();
    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelBoundary();
}
