using System.Globalization;
using System.Text;

namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>
/// Utilidades de lectura de celdas de los insumos (los Excel que ingiere el informe de valor).
/// Comunes a <see cref="CasosParser"/> y <see cref="BitcostParser"/>: ambos leen filas de
/// <see cref="XlsxRowReader"/> con el mismo formato de celda cruda y necesitan las mismas
/// conversiones.
/// </summary>
internal static class InsumoCellUtils
{
    /// <summary>
    /// Las filas del lector tienen largo variable: los huecos del final no se rellenan hasta el
    /// ancho de la cabecera (XlsxRowReader solo reserva hasta la última celda no vacía de esa
    /// fila). Un índice fuera de rango es, por lo tanto, una celda vacía legítima, no un error.
    /// </summary>
    internal static string Get(string[] row, int idx) =>
        idx >= 0 && idx < row.Length ? row[idx].Trim() : string.Empty;

    /// <summary>Normaliza para comparar cabeceras: sin acentos, en minúsculas, espacios simples.</summary>
    internal static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    internal static bool TryDecimal(string raw, out decimal value) =>
        decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Recorta al ancho de la columna. El hash ya se calculó sobre el valor completo.</summary>
    internal static string? Trunc(string s, int max, ref int counter)
    {
        if (s.Length == 0) return null;
        if (s.Length <= max) return s;
        counter++;
        return s[..max];
    }
}
