using System.Globalization;
using static OptimizacionCostos.Api.Features.InformeValor.InsumoCellUtils;

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

    private const string ErrorFormatoBitcost =
        "El archivo no tiene la forma del export de BITCOST. Deben estar las columnas "
        + "Recurso, PVP y la jerarquía de fechas con Año y Mes.";

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
        int total = 0, skipped = 0, truncated = 0, fusionadas = 0;
        string[]? hdr = null;
        int cTen = -1, cSubN = -1, cSubI = -1, cRg = -1, cRes = -1, cCc = -1,
            cCat = -1, cSub = -1, cSrv = -1, cQty = -1, cUni = -1, cRate = -1, cPvp = -1, cAnio = -1, cMes = -1;

        foreach (var row in XlsxRowReader.Read(stream, MaxRows))
        {
            if (hdr is null)
            {
                // Se saltea toda fila con menos de 3 celdas no vacías: son filas decorativas o de
                // título (p. ej. el nombre del informe, un patrón común en exports de Power BI)
                // que pueden aparecer antes de la cabecera real. La primera fila que supere ese
                // umbral se toma como cabecera, aunque no tenga la forma esperada; si no mapea
                // columnas, cae en el throw de más abajo.
                if (row.Count(x => !string.IsNullOrWhiteSpace(x)) < 3) continue;
                hdr = row;
                cTen = Col(hdr, "tenant"); cSubN = Col(hdr, "nombre suscripcion");
                cSubI = Col(hdr, "id suscripcion"); cRg = Col(hdr, "grupo de recursos");
                cRes = Col(hdr, "recurso"); cCc = Col(hdr, "centro de costo");
                cCat = Col(hdr, "categoria"); cSub = Col(hdr, "subcategoria");
                cSrv = Col(hdr, "servicio"); cQty = Col(hdr, "cantidad");
                cUni = Col(hdr, "unidad"); cRate = Col(hdr, "tarifa"); cPvp = Col(hdr, "pvp");
                cAnio = Col(hdr, "jerarquia de fechas ano"); cMes = Col(hdr, "jerarquia de fechas mes");
                if (cPvp < 0 || cRes < 0 || cAnio < 0 || cMes < 0)
                    throw new InvalidOperationException(ErrorFormatoBitcost);
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
            decimal? qty = TryDecimal(Get(row, cQty), out var q) ? q : null;
            decimal? rate = TryDecimal(Get(row, cRate), out var t) ? t : null;

            var hash = NaturalKey.Hash(subI, rg, res, cc, cat, sub, srv, uni,
                anio.ToString(CultureInfo.InvariantCulture), mes.ToString(CultureInfo.InvariantCulture));

            if (acumulado.TryGetValue(hash, out var previa))
            {
                // La fila no se descarta (a diferencia de CasosParser: acá SÍ hay algo que sumar),
                // así que no corresponde contarla en "skipped". Pero tampoco puede desaparecer sin
                // dejar rastro: fusionadas la hace visible en el aviso de más abajo, la pieza que
                // le cierra al consultor la cuenta total = procesadas + descartadas + fusionadas.
                fusionadas++;
                acumulado[hash] = previa with
                {
                    Pvp = previa.Pvp + pvp,
                    // Quantity es aditiva igual que Pvp: dos filas con la misma clave natural
                    // (mismo recurso, categoría, unidad, período...) son el mismo consumo
                    // repetido, así que sus cantidades también se suman.
                    Quantity = previa.Quantity + qty,
                    // Rate es un precio UNITARIO, no algo que se acumule: sumar dos tarifas daría
                    // un número sin sentido de negocio. Se conserva solo si TODAS las filas
                    // fusionadas traen exactamente el mismo valor; la comparación encadenada
                    // (previa.Rate, ya reducido por fusiones anteriores, contra la nueva fila)
                    // hace que en cuanto una difiere el resultado quede en null para siempre: una
                    // fila posterior que sí coincida con la original ya no lo "revive".
                    Rate = previa.Rate == rate ? rate : null,
                };
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
                qty,
                Trunc(uni, 100, ref truncated),
                rate,
                pvp, anio, mes);
        }

        if (hdr is null) throw new InvalidOperationException(ErrorFormatoBitcost);
        if (fusionadas > 0) warnings.Add(
            $"{fusionadas} filas se fusionaron con otra de la misma clave natural: se sumaron sus cantidades e importes.");
        if (truncated > 0) warnings.Add($"{truncated} valores se recortaron por exceder el largo de su columna.");

        return new ParseResult<FacturacionRow>(
            acumulado.Values.ToList(), total, skipped, fusionadas, truncated, warnings);
    }

    /// <summary>Coincidencia exacta sobre el nombre normalizado (sin acentos ni signos).</summary>
    private static int Col(string[] hdr, string wanted)
    {
        for (var i = 0; i < hdr.Length; i++)
            if (Norm(hdr[i]) == wanted) return i;
        return -1;
    }

    private static byte Mes(string raw)
    {
        var n = Norm(raw);
        if (Meses.TryGetValue(n, out var m)) return m;
        return byte.TryParse(n, out var num) && num >= 1 && num <= 12 ? num : (byte)0;
    }
}
