namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>
/// Traductor de estados/orígenes/notas al español para el Excel v3. Port literal de
/// STATUS_META, PRICING_META y translateNote() de innovacion-CDC/src/lib/costs.ts (fuente
/// de verdad): el Excel y el dashboard deben decir exactamente lo mismo.
/// </summary>
public static class CostLabels
{
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

    /// <summary>sql_vm es interno: se agrupa bajo "vms" (visibleServiceKey de costs.ts).
    /// Public para reutilizar en BuildServiceSummaries y evitar duplicación.</summary>
    public static string VisibleServiceKey(string? key) =>
        string.Equals(key, "sql_vm", StringComparison.OrdinalIgnoreCase) ? "vms" : (key ?? "");

    private static object? GetOrNull(IDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) ? v : null;

    private static bool AsBool(object? value)
    {
        if (value is null || value is DBNull) return false;
        if (value is bool b) return b;
        return Convert.ToBoolean(value);
    }
}
