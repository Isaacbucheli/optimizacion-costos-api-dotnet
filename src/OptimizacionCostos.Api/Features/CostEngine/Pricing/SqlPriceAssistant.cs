using System.Text.Json;
using OptimizacionCostos.Api.Configuration;

namespace OptimizacionCostos.Api.Features.CostEngine.Pricing;

/// <summary>
/// Implementación del asistente de precios con Azure OpenAI. Port 1:1 de
/// app/pricing/openai_price_assistant.py::select_price_candidate. Guardrails:
/// solo elige de los candidatos dados (índice), confianza ≥ <see cref="MinConfidence"/>,
/// y audita SIEMPRE (acepte o no). Si la IA falla o no devuelve JSON, retorna null
/// (queda sin precio, igual que con el asistente deshabilitado).
/// </summary>
public sealed class SqlPriceAssistant(
    AppConfig config,
    IChatCompletionClient chat,
    IPriceAssistantAudit audit) : IPriceAssistant
{
    private const int MaxCandidates = 40;
    private const double MinConfidence = 0.6; // modo "automático + auditado"

    public bool IsEnabled =>
        config.AzureOpenAiEnabled
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiEndpoint)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiApiKey)
        && !string.IsNullOrWhiteSpace(config.AzureOpenAiDeployment)
        && config.PricingAssistMode == "assist_match";

    public PriceRow? SelectCandidate(
        string serviceKey,
        string component,
        IReadOnlyDictionary<string, object?> context,
        IReadOnlyList<PriceRow> candidates)
    {
        if (!IsEnabled || candidates.Count == 0) return null;

        // Deduplicar: la cache acumula meters repetidos (mismo producto/meter/precio). Sin esto,
        // el tope de candidatos se llena de duplicados y el meter correcto queda fuera de la lista
        // que ve la IA (causa raíz de un mal pick / rechazo). Dedup por identidad de precio.
        var distinct = candidates
            .GroupBy(c => (c.ProductName, c.MeterName, c.SkuName, c.RetailPrice, c.PriceType, c.ReservationTerm, c.UnitOfMeasure))
            .Select(g => g.First())
            .ToList();

        var trimmed = distinct.Count > MaxCandidates ? distinct.Take(MaxCandidates).ToList() : distinct;

        string? raw = null;
        int? selectedIndex = null;
        double? confidence = null;
        string reason = "";
        PriceRow? selected = null;
        var accepted = false;

        try
        {
            var (system, userJson) = BuildPrompt(serviceKey, component, context, trimmed);
            raw = chat.Complete(system, userJson);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                using var doc = JsonDocument.Parse(StripFences(raw));
                var root = doc.RootElement;
                if (root.TryGetProperty("candidate_index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number)
                    selectedIndex = idxEl.GetInt32();
                if (root.TryGetProperty("confidence", out var confEl) && confEl.ValueKind == JsonValueKind.Number)
                    confidence = confEl.GetDouble();
                if (root.TryGetProperty("reason_es", out var rEl) && rEl.ValueKind == JsonValueKind.String)
                    reason = rEl.GetString() ?? "";

                if (selectedIndex is int idx && (confidence ?? 0) >= MinConfidence && idx >= 0 && idx < trimmed.Count)
                {
                    selected = trimmed[idx] with
                    {
                        AiMatchStrategy = $"assist_match:{component}",
                        AiMatchConfidence = confidence,
                        AiMatchReason = reason,
                    };
                    accepted = true;
                }
            }
        }
        catch
        {
            // Paridad con Python: si la IA falla o no responde JSON válido, se ignora.
            if (string.IsNullOrEmpty(reason)) reason = "Asistente de precios no disponible";
        }

        audit.Record(new PriceAssistantAuditEntry(
            serviceKey, component, SafeJson(context), trimmed.Count,
            selected?.PriceId, selectedIndex, confidence, accepted, reason, raw));

        return selected;
    }

    private static string StripFences(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            t = t.Trim('`').Trim();
            if (t.StartsWith("json", StringComparison.OrdinalIgnoreCase)) t = t[4..].Trim();
        }
        return t;
    }

    private static (string System, string UserJson) BuildPrompt(
        string serviceKey,
        string component,
        IReadOnlyDictionary<string, object?> context,
        IReadOnlyList<PriceRow> candidates)
    {
        const string system =
            "Eres un asistente de validacion de precios Azure. "
            + "Solo puedes seleccionar un candidato de la lista recibida. "
            + "Nunca inventes precios, SKUs, medidores ni valores. "
            + "Si ningun candidato corresponde, devuelve candidate_index null. "
            + "Responde exclusivamente JSON valido.";

        var candidateList = candidates.Select((c, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["price_id"] = c.PriceId,
            ["service_name"] = c.ServiceName,
            ["product_name"] = c.ProductName,
            ["sku_name"] = c.SkuName,
            ["arm_sku_name"] = c.ArmSkuName,
            ["meter_name"] = c.MeterName,
            ["arm_region_name"] = c.ArmRegionName,
            ["price_type"] = c.PriceType,
            ["reservation_term"] = c.ReservationTerm,
            ["unit_of_measure"] = c.UnitOfMeasure,
            ["retail_price"] = c.RetailPrice,
        }).ToList();

        var user = new Dictionary<string, object?>
        {
            ["task"] = "Selecciona el mejor precio real de Azure Retail Prices API para el componente solicitado.",
            ["service_key"] = serviceKey,
            ["component"] = component,
            ["resource_context"] = context,
            ["selection_rules"] = new[]
            {
                "El candidato debe coincidir con producto, region y unidad del componente.",
                "Para PAYG compute prefiere Consumption y unidades por hora.",
                "Para storage prefiere Consumption y unidades GB/Month o Data Stored.",
                "Para RI prefiere Reservation con reservation_term solicitado.",
                "Si el match es ambiguo o incorrecto, usa candidate_index null.",
            },
            ["response_schema"] = new Dictionary<string, object?>
            {
                ["candidate_index"] = "integer or null",
                ["confidence"] = "number from 0 to 1",
                ["reason_es"] = "explicacion breve en espanol",
            },
            ["candidates"] = candidateList,
        };

        return (system, JsonSerializer.Serialize(user));
    }

    private static string SafeJson(object o)
    {
        try
        {
            var s = JsonSerializer.Serialize(o);
            return s.Length > 4000 ? s[..4000] : s;
        }
        catch
        {
            return "";
        }
    }
}
