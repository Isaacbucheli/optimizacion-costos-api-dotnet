namespace OptimizacionCostos.Api.Data;

/// <summary>
/// Valida que los campos de texto quepan en su columna ANTES de armar el UPDATE. Sin esto, un valor
/// más largo que el NVARCHAR(n) hace que SQL Server lance el error 8152 ("String or binary data
/// would be truncated"): nadie maneja esa excepción, Kestrel corta la conexión y el cliente ve un
/// socket cerrado en vez de un 400. Un escáner lee esa conexión cerrada como un fallo de la
/// aplicación — el ZAP del 2026-08-03 la reportó como CWE-134 (Format String) en los 4 campos de
/// policy_catalog cuyo ancho era menor que el payload, y en ninguno de los que sí cabían.
///
/// El límite se declara junto a la whitelist de columnas de cada catálogo para que esquema y
/// validación se muevan juntos. Las columnas NVARCHAR(MAX) no van en el mapa: no tienen límite
/// práctico y omitirlas es lo que las deja pasar sin chequeo.
/// </summary>
public static class ColumnLimits
{
    /// <summary>
    /// Primer campo que excede el ancho de su columna, con el mensaje listo para un 400, o
    /// <c>null</c> si todos caben. Ignora los valores no-string (int, bool, null) y las columnas
    /// ausentes del mapa.
    /// </summary>
    public static string? FirstViolation(
        IReadOnlyDictionary<string, object?> fields,
        IReadOnlyDictionary<string, int> maxLengths)
    {
        foreach (var (column, value) in fields)
        {
            if (value is not string text) continue;
            if (!maxLengths.TryGetValue(column, out var max)) continue;
            if (text.Length > max)
                return $"El campo '{column}' excede el máximo de {max} caracteres";
        }
        return null;
    }
}
