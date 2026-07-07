using System.Text.RegularExpressions;

namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>
/// Traductor de estados/orígenes/notas al español para el Excel v3. Port literal de
/// STATUS_META, PRICING_META y translateNote() de innovacion-CDC/src/lib/costs.ts (fuente
/// de verdad): el Excel y el dashboard deben decir exactamente lo mismo.
/// </summary>
public static class CostLabels
{
    /// <summary>Patrón técnico crudo (ej. "sku=B1 region=eastus2...") que NUNCA debe exponerse tal cual.</summary>
    private static readonly Regex RawTechnicalNote = new(@"^\s*sku=", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Port de STATUS_META (costs.ts): estado crudo → etiqueta en español.</summary>
    private static readonly Dictionary<string, string> StatusMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["calculated"] = "Calculado",
        ["variable_pricing"] = "Precio variable",
        ["price_not_found"] = "Precio no encontrado",
        ["manual_required"] = "Requiere costo manual",
        ["not_applicable"] = "No aplica",
        ["not_running"] = "No encendida",
        ["error"] = "Error",
    };

    /// <summary>
    /// Estado de cálculo en español. Port de statusMeta().label: estado desconocido → valor crudo;
    /// null/vacío → "" (a diferencia del dashboard, que usa "-"; el Excel deja la celda vacía).
    /// </summary>
    public static string StatusEs(string? status)
    {
        if (string.IsNullOrEmpty(status)) return "";
        return StatusMap.TryGetValue(status, out var label) ? label : status;
    }

    /// <summary>
    /// Origen del precio. Port exacto de pricingKind() (costs.ts líneas 109-115).
    /// Precedencia: (1) calculation_notes contiene "assist_match" → "IA asistida" (MÁXIMA);
    /// (2) calculation_status == "manual_required" → "Manual";
    /// (3) calculation_status == "calculated" → "Exacto";
    /// (4) else → "-".
    /// Nota: ignore is_manual_cost (dashboard no lo lee); estos estados de cálculo son la fuente única.
    /// </summary>
    public static string PriceOrigin(IDictionary<string, object?> row)
    {
        var notes = GetOrNull(row, "calculation_notes") as string ?? "";
        if (notes.Contains("assist_match", StringComparison.OrdinalIgnoreCase)) return "IA asistida";

        var status = GetOrNull(row, "calculation_status") as string;
        if (string.Equals(status, "manual_required", StringComparison.OrdinalIgnoreCase)) return "Manual";
        if (string.Equals(status, "calculated", StringComparison.OrdinalIgnoreCase)) return "Exacto";

        return "-";
    }

    /// <summary>
    /// Nota legible en español. Port de translateNote() (costs.ts): arma un texto por servicio a
    /// partir de tokens de calculation_notes (sku=, category=, capacity=, tier=, storage_gb=).
    /// Si no hay notas ni servicio reconocido, cae a manual_cost_note y por último "". La nota cruda
    /// SIN traducir (ej. "sku=B1 region=eastus2...") nunca se expone: si matchea el patrón técnico,
    /// se traduce igual por servicio o se devuelve "".
    /// </summary>
    public static string NoteEs(IDictionary<string, object?> row)
    {
        var raw = GetOrNull(row, "calculation_notes") as string ?? "";
        var service = VisibleServiceKey(GetOrNull(row, "service_key") as string);

        var sku = Capture(raw, "sku");
        var category = Capture(raw, "category");
        var capacity = Capture(raw, "capacity");
        var tier = Capture(raw, "tier");
        var storage = Capture(raw, "storage_gb");

        switch (service)
        {
            case "public_ip":
            {
                var assoc = string.Equals(category, "orphan", StringComparison.OrdinalIgnoreCase)
                    ? "sin recurso asociado" : "asociada a un recurso activo";
                return $"IP pública {(string.IsNullOrEmpty(sku) ? "Azure" : sku)} {assoc}. Costo mensual estimado con Azure Retail Prices API.";
            }
            case "appservice":
            {
                var instances = string.IsNullOrEmpty(capacity) ? "capacidad detectada" : $"{capacity} instancia(s)";
                return $"Plan {(string.IsNullOrEmpty(sku) ? "App Service" : sku)} con {instances}. Precio mensual calculado según región y sistema operativo.";
            }
            case "mysql":
            {
                var storageText = string.IsNullOrEmpty(storage) ? "" : $" y {storage} GB de almacenamiento";
                return $"Servidor MySQL Flexible {tier ?? ""}{storageText}. Compute y storage calculados por separado.";
            }
            case "redis":
                return $"Cache Redis {sku ?? ""}. Precio seleccionado por SKU, familia y capacidad.";
            case "disks":
                return $"Disco administrado {sku ?? ""}. Precio mensual estimado por tipo, tier y tamaño provisionado.";
            case "sql":
                return "Base Azure SQL calculada según tier, modelo de compra y capacidad configurada.";
            case "vms":
                return "Máquina virtual calculada por tamaño, región, sistema operativo y estado de ejecución.";
        }

        // Sin servicio reconocido: si la nota cruda es el patrón técnico "sku=..." NUNCA se expone.
        if (!string.IsNullOrEmpty(raw) && !RawTechnicalNote.IsMatch(raw)) return raw;

        return (GetOrNull(row, "manual_cost_note") as string) ?? "";
    }

    /// <summary>
    /// Etiqueta de reserva confirmada para el Excel (más explícita que riTooltip() de costs.ts, que
    /// solo arma el tooltip "nombre · término" del dashboard). "" si no está confirmada; si lo está:
    /// "Reservado" a secas, "Reservado · {nombre}" si falta el término, o "Reservado · {nombre} ({término})"
    /// cuando hay ambos. Sin nombre pero con término: "Reservado ({término})".
    /// </summary>
    public static string ReservedLabel(IDictionary<string, object?> row)
    {
        var coverage = GetOrNull(row, "ri_coverage") as string;
        if (!string.Equals(coverage, "confirmed", StringComparison.OrdinalIgnoreCase)) return "";

        var name = GetOrNull(row, "ri_reservation_name") as string;
        var term = GetOrNull(row, "ri_term") as string;
        var hasName = !string.IsNullOrEmpty(name);
        var hasTerm = !string.IsNullOrEmpty(term);

        if (hasName && hasTerm) return $"Reservado · {name} ({term})";
        if (hasName) return $"Reservado · {name}";
        if (hasTerm) return $"Reservado ({term})";
        return "Reservado";
    }

    /// <summary>sql_vm es interno: se agrupa bajo "vms" (visibleServiceKey de costs.ts).</summary>
    private static string VisibleServiceKey(string? key) =>
        string.Equals(key, "sql_vm", StringComparison.OrdinalIgnoreCase) ? "vms" : (key ?? "");

    private static string? Capture(string raw, string token)
    {
        var m = Regex.Match(raw, $@"{token}=(\S+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static object? GetOrNull(IDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) ? v : null;

    private static bool AsBool(object? value)
    {
        if (value is null || value is DBNull) return false;
        if (value is bool b) return b;
        return Convert.ToBoolean(value);
    }
}
