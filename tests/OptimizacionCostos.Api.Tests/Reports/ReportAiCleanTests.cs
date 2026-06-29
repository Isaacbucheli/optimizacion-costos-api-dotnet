using System.Text.Json;
using OptimizacionCostos.Api.Features.Reports;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Reports;

/// <summary>
/// Tests de fidelidad de ReportAiClean.Clean (port de ai_curator.py::_clean) tras las correcciones
/// del review B8: NFKC (no NFC), rango de emojis exacto U+1F300-U+1FAFF, y str(value or "") con
/// colapso de valores 'falsy' (0/false/[]/{}).
/// </summary>
public class ReportAiCleanTests
{
    private static JsonElement El<T>(T value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Clean_AplicaNfkc_DescomposicionDeCompatibilidad()
    {
        // NFKC pliega el superíndice ² (U+00B2) a "2"; NFC lo conservaría.
        Assert.Equal("m2", ReportAiClean.Clean(El("m²"), 100));
        // Ligadura fi (U+FB01) -> "fi" con NFKC.
        Assert.Equal("fi", ReportAiClean.Clean(El("ﬁ"), 100));
    }

    [Fact]
    public void Clean_ColapsaValoresFalsy_AStringVacio()
    {
        // str(value or "") de Python: 0/0.0/false/[]/{} son falsy -> "".
        Assert.Equal("", ReportAiClean.Clean(El(0), 100));
        Assert.Equal("", ReportAiClean.Clean(El(0.0), 100));
        Assert.Equal("", ReportAiClean.Clean(El(false), 100));
        Assert.Equal("", ReportAiClean.Clean(El(Array.Empty<int>()), 100));
        Assert.Equal("", ReportAiClean.Clean(El(new Dictionary<string, int>()), 100));
    }

    [Fact]
    public void Clean_ValoresTruthy_SeConservan()
    {
        Assert.Equal("5", ReportAiClean.Clean(El(5), 100));
        Assert.Equal("True", ReportAiClean.Clean(El(true), 100));
        Assert.Equal("Hola mundo", ReportAiClean.Clean(El("Hola mundo"), 100));
    }

    [Fact]
    public void Clean_QuitaEmojisEnRangoPython_PeroConservaFueraDeRango()
    {
        // 🚀 (U+1F680) está en U+1F300-U+1FAFF -> se elimina.
        Assert.Equal("ab", ReportAiClean.Clean(El("a\U0001F680b"), 100));
        // 🀄 (U+1F004) está FUERA del rango Python (U+1F000-U+1F2FF) -> se conserva
        // (el rango .NET viejo lo borraba de más).
        Assert.Equal("a\U0001F004b", ReportAiClean.Clean(El("a\U0001F004b"), 100));
    }

    [Fact]
    public void Clean_RespetaLimite()
    {
        Assert.Equal("abc", ReportAiClean.Clean(El("abcdef"), 3));
    }
}
