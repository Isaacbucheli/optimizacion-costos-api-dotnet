using System.Globalization;

namespace OptimizacionCostos.Api.Features.InformeValor;

/// <summary>
/// Parser del export "tabla de hechos" del Power BI de facturación (BITCOST).
/// El grano incluye categoría, subcategoría, servicio, centro de costo y unidad, así que
/// un mismo recurso aparece varias veces en el mismo mes: la clave natural incluye todas
/// esas dimensiones y las filas realmente idénticas se suman en memoria.
/// </summary>
public static class BitcostParser
{
    public const int MaxRows = 400_000;

    private static readonly Dictionary<string, byte> Meses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enero"] = 1, ["febrero"] = 2, ["marzo"] = 3, ["abril"] = 4, ["mayo"] = 5, ["junio"] = 6,
        ["julio"] = 7, ["agosto"] = 8, ["septiembre"] = 9, ["setiembre"] = 9, ["octubre"] = 10,
        ["noviembre"] = 11, ["diciembre"] = 12,
    };

    public static ParseResult<FacturacionRow> Parse(Stream stream)
    {
        var acumulado = new Dictionary<string, FacturacionRow>(StringComparer.Ordinal);
        var warnings = new List<string>();
        int total = 0, skipped = 0, truncated = 0;
        string[]? hdr = null;
        int cTen = -1, cSubN = -1, cSubI = -1, cRg = -1, cRes = -1, cCc = -1,
            cCat = -1, cSub = -1, cSrv = -1, cQty = -1, cUni = -1, cRate = -1, cPvp = -1, cAnio = -1, cMes = -1;

        foreach (var row in XlsxRowReader.Read(stream, MaxRows))
        {
            if (hdr is null)
            {
                // Solo se salta una fila realmente en blanco (posible en un sheet exportado con
                // filas vacías al inicio); una fila con contenido, aunque no tenga la forma
                // esperada, ya cuenta como cabecera y debe caer en el throw de más abajo.
                if (row.All(x => string.IsNullOrWhiteSpace(x))) continue;
                hdr = row;
                cTen = Col(hdr, "tenant"); cSubN = Col(hdr, "nombre suscripcion");
                cSubI = Col(hdr, "id suscripcion"); cRg = Col(hdr, "grupo de recursos");
                cRes = Col(hdr, "recurso"); cCc = Col(hdr, "centro de costo");
                cCat = Col(hdr, "categoria"); cSub = Col(hdr, "subcategoria");
                cSrv = Col(hdr, "servicio"); cQty = Col(hdr, "cantidad");
                cUni = Col(hdr, "unidad"); cRate = Col(hdr, "tarifa"); cPvp = Col(hdr, "pvp");
                cAnio = Col(hdr, "jerarquia de fechas ano"); cMes = Col(hdr, "jerarquia de fechas mes");
                if (cPvp < 0 || cRes < 0 || cAnio < 0 || cMes < 0)
                    throw new InvalidOperationException(
                        "El archivo no tiene la forma del export de BITCOST. Deben estar las columnas "
                        + "Recurso, PVP y la jerarquía de fechas con Año y Mes.");
                continue;
            }

            total++;
            var res = Get(row, cRes);
            var rg = Get(row, cRg);
            if (res.Length == 0 && rg.Length == 0) { skipped++; continue; }

            if (!TryDecimal(Get(row, cPvp), out var pvp)) { skipped++; continue; }
            if (!short.TryParse(Get(row, cAnio), NumberStyles.Integer, CultureInfo.InvariantCulture, out var anio)
                || anio < 2000 || anio > 2100) { skipped++; continue; }
            var mes = Mes(Get(row, cMes));
            if (mes == 0) { skipped++; continue; }

            var subI = Get(row, cSubI); var cc = Get(row, cCc); var cat = Get(row, cCat);
            var sub = Get(row, cSub); var srv = Get(row, cSrv); var uni = Get(row, cUni);

            var hash = NaturalKey.Hash(subI, rg, res, cc, cat, sub, srv, uni,
                anio.ToString(CultureInfo.InvariantCulture), mes.ToString(CultureInfo.InvariantCulture));

            if (acumulado.TryGetValue(hash, out var previa))
            {
                acumulado[hash] = previa with { Pvp = previa.Pvp + pvp };
                continue;
            }

            acumulado[hash] = new FacturacionRow(
                hash,
                Trunc(Get(row, cTen), 100, ref truncated),
                Trunc(Get(row, cSubN), 200, ref truncated),
                Trunc(subI, 100, ref truncated),
                Trunc(rg, 255, ref truncated),
                Trunc(res, 512, ref truncated),
                Trunc(cc, 200, ref truncated),
                Trunc(cat, 200, ref truncated),
                Trunc(sub, 200, ref truncated),
                Trunc(srv, 200, ref truncated),
                TryDecimal(Get(row, cQty), out var q) ? q : null,
                Trunc(uni, 100, ref truncated),
                TryDecimal(Get(row, cRate), out var t) ? t : null,
                pvp, anio, mes);
        }

        if (hdr is null) throw new InvalidOperationException("El archivo está vacío.");
        if (truncated > 0) warnings.Add($"{truncated} valores se recortaron por exceder el largo de su columna.");

        return new ParseResult<FacturacionRow>(
            acumulado.Values.ToList(), total, skipped, truncated, warnings);
    }

    /// <summary>Coincidencia exacta sobre el nombre normalizado (sin acentos ni signos).</summary>
    private static int Col(string[] hdr, string wanted)
    {
        for (var i = 0; i < hdr.Length; i++)
            if (Norm(hdr[i]) == wanted) return i;
        return -1;
    }

    internal static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Get(string[] row, int idx) =>
        idx >= 0 && idx < row.Length ? row[idx].Trim() : string.Empty;

    private static byte Mes(string raw)
    {
        var n = Norm(raw);
        if (Meses.TryGetValue(n, out var m)) return m;
        return byte.TryParse(n, out var num) && num >= 1 && num <= 12 ? num : (byte)0;
    }

    private static bool TryDecimal(string raw, out decimal value) =>
        decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Recorta al ancho de la columna. El hash ya se calculó sobre el valor completo.</summary>
    private static string? Trunc(string s, int max, ref int counter)
    {
        if (s.Length == 0) return null;
        if (s.Length <= max) return s;
        counter++;
        return s[..max];
    }
}
