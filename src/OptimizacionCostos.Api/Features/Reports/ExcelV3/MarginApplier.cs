namespace OptimizacionCostos.Api.Features.Reports.ExcelV3;

/// <summary>
/// Aplicador de margen comercial para el Excel v3: multiplica las columnas monetarias por
/// (1 + margen%) sin tocar porcentajes (*_pct), que son invariantes bajo un margen uniforme
/// (se aplica el mismo factor a PAYG y a RI, así que el ahorro relativo no cambia).
/// </summary>
public static class MarginApplier
{
    /// <summary>Claves monetarias sobre las que aplica el margen (case-insensitive, vía el diccionario canónico).</summary>
    public static readonly string[] MonetaryKeys =
    {
        "payg_monthly", "payg_hourly", "ri_1y_monthly", "ri_3y_monthly",
        "savings_1y_monthly", "savings_3y_monthly", "manual_monthly_cost", "sql_addon_monthly",
        "ahb_discount_monthly", "storage_monthly", "monthly_cost", "total_monthly", "total_annual",
        "savings_monthly", "savings_annual",
    };

    /// <summary>
    /// Multiplica in-place cada clave de <see cref="MonetaryKeys"/> presente y numérica en cada fila
    /// por (1 + margenPct/100). Ignora claves ausentes, null o DBNull. NUNCA toca *_pct (no están
    /// en MonetaryKeys) ni otras claves (texto, ids, etc.).
    /// </summary>
    public static void ApplyInPlace(IEnumerable<Dictionary<string, object?>> rows, decimal marginPct)
    {
        foreach (var row in rows)
        {
            foreach (var key in MonetaryKeys)
            {
                if (!row.TryGetValue(key, out var value)) continue;
                if (!TryAsDouble(value, out var d)) continue;
                row[key] = Apply(d, marginPct);
            }
        }
    }

    /// <summary>Aplica el margen a un único valor: value * (1 + margenPct/100).</summary>
    public static double Apply(double value, decimal marginPct) => value * (1 + (double)marginPct / 100);

    /// <summary>Convierte a double solo si el valor es numérico (double/decimal/int/float); false para null/DBNull/texto.</summary>
    private static bool TryAsDouble(object? value, out double result)
    {
        switch (value)
        {
            case null:
            case DBNull:
                result = 0;
                return false;
            case double or decimal or int or float or long:
                result = Convert.ToDouble(value);
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
