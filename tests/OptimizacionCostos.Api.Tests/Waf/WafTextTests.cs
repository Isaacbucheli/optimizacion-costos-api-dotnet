using OptimizacionCostos.Api.Features.Waf;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Waf;

/// <summary>
/// Paridad de los helpers de texto WAF contra Python real (difflib.SequenceMatcher.ratio,
/// unicodedata NFKD + ascii). Si esto pasa, el scoring de dedup/consolidación coincide 1:1.
/// </summary>
public sealed class WafTextTests
{
    [Theory]
    [InlineData("Héhabilitar  Escalado Automático!!!  ", "hehabilitar escalado automatico")]
    [InlineData("Azure Advisor: Recommendation (Preview)", "azure advisor recommendation preview")]
    [InlineData("", "")]
    public void NormalizeText_IgualQuePython(string input, string expected)
        => Assert.Equal(expected, WafText.NormalizeText(input));

    [Theory]
    // (a, b, ratioEsperado-de-Python difflib)
    [InlineData("Enable autoscale for Virtual Machine Scale Sets", "Habilitar escalado automatico en conjuntos de escalas", 0.3)]
    [InlineData("Habilitar escalado automatico", "Habilitar escalado automatico en VMSS", 0.878788)]
    [InlineData("Enable soft delete for blobs", "Enable soft delete for blobs", 1.0)]
    [InlineData("abc", "xyz", 0.0)]
    public void SequenceRatio_IgualQueDifflib(string a, string b, double expected)
        => Assert.Equal(expected, WafText.SequenceRatio(a, b), 5);

    [Fact]
    public void Tokens_ExcluyeStopwordsYCortos()
    {
        var t = WafText.Tokens("Azure Advisor Recommendation for Resource");
        // azure/for/resource/recommendation son stopwords; quedan advisor (>2) — "for" stopword
        Assert.Contains("advisor", t);
        Assert.DoesNotContain("azure", t);
        Assert.DoesNotContain("for", t);
    }

    [Fact]
    public void Jaccard_YResourceOverlap()
    {
        var a = new HashSet<string> { "x", "y", "z" };
        var b = new HashSet<string> { "y", "z", "w" };
        Assert.Equal(2.0 / 4.0, WafText.Jaccard(a, b), 5); // |∩|=2 |∪|=4
        Assert.Equal(2.0 / 3.0, WafText.ResourceOverlap(a, b), 5); // |∩|=2 / min(3,3)=3
        Assert.Equal(0.0, WafText.Jaccard(a, new HashSet<string>()));
    }

    [Fact]
    public void ResourceSet_SeparaPorSaltosYComas()
    {
        var s = WafText.ResourceSet("vm-1, vm-2\nvm-3;vm-4|vm-5");
        Assert.Equal(5, s.Count);
        Assert.Contains("vm 1", s); // normalizado: '-' → espacio
    }
}
