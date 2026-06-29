using System.Text.Json.Nodes;
using OptimizacionCostos.Api.Features.Inventory;
using Xunit;

namespace OptimizacionCostos.Api.Tests.Inventory;

/// <summary>
/// Lectura tipada de filas de Resource Graph. Las columnas de la KQL llegan como número, string
/// o bool; el lector debe tolerar cada caso igual que <c>row.get(...)</c> de Python.
/// </summary>
public sealed class RgRowTests
{
    private static RgRow Row(string json) => new(JsonNode.Parse(json));

    [Fact]
    public void Str_LeeStringYNumeroComoTexto()
    {
        var r = Row("""{"name":"vm-1","skuCapacity":4}""");
        Assert.Equal("vm-1", r.Str("name"));
        Assert.Null(r.Str("missing"));
    }

    [Fact]
    public void Int_Long_Dbl_ParseanNumeroYString()
    {
        var r = Row("""{"diskSizeGB":128,"maxSizeBytes":34359738368,"perDbMax":1.5,"capStr":"8"}""");
        Assert.Equal(128, r.Int("diskSizeGB"));
        Assert.Equal(34359738368L, r.Long("maxSizeBytes"));
        Assert.Equal(1.5, r.Dbl("perDbMax"));
        Assert.Equal(8, r.Int("capStr")); // número como string
        Assert.Null(r.Int("missing"));
    }

    [Fact]
    public void BoolN_InterpretaTruthyYNullSiAusente()
    {
        var r = Row("""{"a":true,"b":false,"c":1,"d":0,"e":"true","f":"false"}""");
        Assert.True(r.BoolN("a"));
        Assert.False(r.BoolN("b"));
        Assert.True(r.BoolN("c"));
        Assert.False(r.BoolN("d"));
        Assert.True(r.BoolN("e"));
        Assert.False(r.BoolN("f"));
        Assert.Null(r.BoolN("missing"));
        Assert.False(r.BoolFalse("missing")); // ausente → false
    }

    [Fact]
    public void Node_YStrList_LeenAnidadosYArrays()
    {
        var r = Row("""{"properties":{"vCores":4},"sample_names":["a","b","c"]}""");
        Assert.NotNull(r.Node("properties"));
        Assert.Equal(["a", "b", "c"], r.StrList("sample_names"));
        Assert.Empty(r.StrList("missing"));
    }
}
