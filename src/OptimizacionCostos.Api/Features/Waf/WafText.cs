using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OptimizacionCostos.Api.Features.Waf;

/// <summary>
/// Helpers de texto compartidos del módulo WAF (port de dedup.py / consolidate.py). Una sola
/// implementación reusada por dedup, consolidación, curación e ingesta. Incluye el port fiel de
/// <c>difflib.SequenceMatcher.ratio()</c> (sin autojunk; los títulos son cortos, &lt;200 chars).
/// </summary>
public static partial class WafText
{
    [GeneratedRegex("[^a-zA-Z0-9]+")] private static partial Regex NonAlnumRun();
    [GeneratedRegex("[\r\n,;|]+")] private static partial Regex ResourceSplit();

    /// <summary>
    /// Normaliza para comparación: NFKD → solo ASCII (descarta acentos/no-ascii como
    /// encode('ascii','ignore')) → no-alfanumérico a espacio → minúsculas → colapsa/trim.
    /// </summary>
    public static string NormalizeText(object? value)
    {
        var s = value?.ToString();
        if (string.IsNullOrEmpty(s)) return "";
        var nfkd = s.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(nfkd.Length);
        foreach (var ch in nfkd)
            if (ch <= 127) sb.Append(ch); // ASCII-only (combining marks de NFKD se descartan)
        var ascii = NonAlnumRun().Replace(sb.ToString(), " ");
        return ascii.ToLowerInvariant().Trim();
    }

    /// <summary>Tokens (len &gt; 2, sin stopwords). Port de _tokens.</summary>
    public static HashSet<string> Tokens(object? value)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in NormalizeText(value).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (t.Length > 2 && !WafConstants.Stopwords.Contains(t)) set.Add(t);
        return set;
    }

    /// <summary>Tokens de título (len &gt; 2, SIN excluir stopwords). Port de _title_tokens.</summary>
    public static HashSet<string> TitleTokens(object? value)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in NormalizeText(value).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (t.Length > 2) set.Add(t);
        return set;
    }

    /// <summary>Jaccard |∩|/|∪| (0 si alguno vacío o unión 0).</summary>
    public static double Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;
        var inter = a.Count(b.Contains);
        var union = a.Count + b.Count - inter;
        return union == 0 ? 0.0 : (double)inter / union;
    }

    /// <summary>Solapamiento de recursos |∩|/min(|a|,|b|) (0 si alguno vacío). Port de _resource_overlap.</summary>
    public static double ResourceOverlap(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;
        var inter = a.Count(b.Contains);
        return (double)inter / Math.Min(a.Count, b.Count);
    }

    /// <summary>Set de nombres de recurso normalizados desde texto (split por saltos/,;|). Port de _resource_set.</summary>
    public static HashSet<string> ResourceSet(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return set;
        foreach (var part in ResourceSplit().Split(text))
        {
            var norm = NormalizeText(part);
            if (norm.Length > 0) set.Add(norm);
        }
        return set;
    }

    /// <summary>
    /// Port fiel de <c>difflib.SequenceMatcher.ratio()</c> = 2·M/T (M = chars en bloques
    /// coincidentes, T = len(a)+len(b)). Sin autojunk (inputs cortos). Sobre texto normalizado
    /// el llamador decide; aquí se compara tal cual se recibe.
    /// </summary>
    public static double SequenceRatio(string? a, string? b)
    {
        a ??= ""; b ??= "";
        var total = a.Length + b.Length;
        if (total == 0) return 1.0;

        var b2j = new Dictionary<char, List<int>>();
        for (var i = 0; i < b.Length; i++)
        {
            if (!b2j.TryGetValue(b[i], out var l)) { l = []; b2j[b[i]] = l; }
            l.Add(i);
        }
        var matches = MatchingChars(a, b2j, 0, a.Length, 0, b.Length);
        return 2.0 * matches / total;
    }

    private static int MatchingChars(string a, Dictionary<char, List<int>> b2j, int alo, int ahi, int blo, int bhi)
    {
        var (i, j, k) = FindLongestMatch(a, b2j, alo, ahi, blo, bhi);
        if (k == 0) return 0;
        return k
            + MatchingChars(a, b2j, alo, i, blo, j)
            + MatchingChars(a, b2j, i + k, ahi, j + k, bhi);
    }

    private static (int Besti, int Bestj, int Bestsize) FindLongestMatch(
        string a, Dictionary<char, List<int>> b2j, int alo, int ahi, int blo, int bhi)
    {
        int besti = alo, bestj = blo, bestsize = 0;
        var j2len = new Dictionary<int, int>();
        for (var i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var js))
                foreach (var j in js)
                {
                    if (j < blo) continue;
                    if (j >= bhi) break;
                    var k = (j2len.TryGetValue(j - 1, out var prev) ? prev : 0) + 1;
                    newj2len[j] = k;
                    if (k > bestsize) { besti = i - k + 1; bestj = j - k + 1; bestsize = k; }
                }
            j2len = newj2len;
        }
        return (besti, bestj, bestsize);
    }
}
