using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

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

    // Grupos de EXACTAMENTE 3 dígitos tras el separador, con el primer grupo sin cero a la
    // izquierda: distingue "1,234" (miles = 1234) de "0,024" (decimal = 0.024: un monto chico no
    // se agrupa con un cero adelante) y de "12,5" (decimal = 12.5: un solo dígito no es un grupo
    // de miles). Es la misma regla que ya usa la plantilla JS para la coma (con el agregado de
    // excluir el cero a la izquierda, que la plantilla no excluye), extendida acá al punto
    // solitario: ver TryDecimal.
    private static readonly Regex MilesConComa = new(@"^-?[1-9]\d{0,2}(,\d{3})+$", RegexOptions.Compiled);
    private static readonly Regex MilesConPunto = new(@"^-?[1-9]\d{0,2}(\.\d{3})+$", RegexOptions.Compiled);

    /// <summary>
    /// Conversión numérica tolerante, única para todo el módulo (BitcostParser y CasosParser):
    /// antes de este fix esto era <c>decimal.TryParse(raw, NumberStyles.Float, ...)</c> sin más,
    /// que no acepta separador de miles ni símbolo de moneda, así que una celda como "1,234.56" o
    /// "$1.234,56" no convertía y la fila desaparecía sin ningún aviso (D13 del plan de la
    /// entrega 2b: un BITCOST con formato de miles daba un informe con el gasto muy por debajo
    /// del real). Reglas, en orden de aplicación:
    ///
    /// <list type="bullet">
    /// <item>Celda vacía (tras recortar espacios, incluidos los finos) devuelve false: no es
    /// cero, es la ausencia de un valor.</item>
    /// <item>El paréntesis contable "(1234)" es negativo. Al revés que la plantilla JS, que lo
    /// convierte en un cargo positivo: eso es un defecto de la plantilla que acá se corrige a
    /// propósito.</item>
    /// <item>Un signo menos al final ("1234-", notación de algunos ERP) también es negativo:
    /// decimal.Parse de .NET solo reconoce el signo por delante.</item>
    /// <item>Se descarta cualquier carácter que no sea dígito, separador, signo o exponente
    /// (símbolo de moneda, espacios normales y finos, letras). Conserva 'e'/'E' con su signo: la
    /// notación científica de un export de facturación (1.6E-05) no se puede tocar sin convertir
    /// un centavo en 1.6 dólares.</item>
    /// <item>Con los dos separadores presentes, el último que aparece es el decimal (mismo
    /// criterio que la plantilla).</item>
    /// <item>Con uno solo, se distingue por forma (ver <see cref="MilesConComa"/>): grupos de
    /// miles se leen como miles, el resto como decimal. Un export en español usa el punto para
    /// miles y la coma para decimales; uno en inglés al revés, y esta regla cubre los dos sin
    /// conocer el locale del archivo.</item>
    /// </list>
    ///
    /// Lo que este método NO resuelve —y no puede, sin el locale del archivo— es la ambigüedad
    /// de un valor con un solo grupo de exactamente 3 dígitos ("2.500"): puede ser 2500 (miles) o
    /// 2.5 (decimal con un cero de relleno). Se lee como miles, igual que la plantilla lee "1,234"
    /// como miles y no como 1.234.
    /// </summary>
    internal static bool TryDecimal(string raw, out decimal value)
    {
        value = 0m;
        if (raw is null) return false;
        var s = raw.Trim();
        if (s.Length == 0) return false; // celda vacía: no hay valor que convertir, no es cero

        var negativo = false;

        // El símbolo de moneda puede quedar DENTRO del paréntesis ("($1,234.56)"), así que el
        // paréntesis se detecta sobre el string recortado, antes de limpiar cualquier otra cosa.
        if (s.Length >= 2 && s[0] == '(' && s[^1] == ')')
        {
            negativo = true;
            s = s[1..^1].Trim();
            if (s.Length == 0) return false;
        }

        var limpio = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (ch is >= '0' and <= '9' or '.' or ',' or 'e' or 'E' or '+' or '-')
                limpio.Append(ch);
        s = limpio.ToString();
        if (s.Length == 0) return false;

        if (s.Length > 1 && s[^1] == '-')
        {
            negativo = true;
            s = s[..^1];
        }

        var hasDot = s.Contains('.');
        var hasComma = s.Contains(',');

        if (hasDot && hasComma)
        {
            // El último separador que aparece es el decimal.
            s = s.LastIndexOf(',') > s.LastIndexOf('.')
                ? s.Replace(".", "").Replace(',', '.')   // "1.234,56" (es) -> "1234.56"
                : s.Replace(",", "");                     // "1,234.56" (en) -> "1234.56"
        }
        else if (hasComma)
        {
            s = MilesConComa.IsMatch(s) ? s.Replace(",", "") : s.Replace(',', '.');
        }
        else if (hasDot && MilesConPunto.IsMatch(s))
        {
            s = s.Replace(".", "");
        }
        // Solo punto y no es agrupación de miles: ya es un decimal válido tal cual (incluida la
        // notación científica), se deja igual.

        if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            value = 0m;
            return false;
        }

        if (negativo) value = -Math.Abs(value);
        return true;
    }

    /// <summary>Recorta al ancho de la columna. El hash ya se calculó sobre el valor completo.</summary>
    internal static string? Trunc(string s, int max, ref int counter)
    {
        if (s.Length == 0) return null;
        if (s.Length <= max) return s;
        counter++;
        return s[..max];
    }
}
