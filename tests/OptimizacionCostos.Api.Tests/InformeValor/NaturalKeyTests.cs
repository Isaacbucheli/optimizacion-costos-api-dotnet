using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

public sealed class NaturalKeyTests
{
    [Fact]
    public void Devuelve_64_hex_en_minuscula()
    {
        var h = NaturalKey.Hash("a", "b");
        Assert.Equal(64, h.Length);
        Assert.Matches("^[0-9a-f]{64}$", h);
    }

    [Fact]
    public void Es_estable_para_las_mismas_partes()
    {
        Assert.Equal(NaturalKey.Hash("a", "b", "c"), NaturalKey.Hash("a", "b", "c"));
    }

    [Fact]
    public void Null_y_cadena_vacia_son_equivalentes()
    {
        Assert.Equal(NaturalKey.Hash("a", null, "c"), NaturalKey.Hash("a", "", "c"));
    }

    [Fact]
    public void El_separador_evita_colisiones_por_concatenacion()
    {
        Assert.NotEqual(NaturalKey.Hash("ab", "c"), NaturalKey.Hash("a", "bc"));
    }

    /// <summary>
    /// El motivo de existir de esta clase: si el hash se calculara sobre el valor ya truncado
    /// al ancho de la columna, dos recursos que difieren después del carácter 512 serían la
    /// misma fila y las altas y bajas del informe saldrían mal sin ninguna señal.
    /// </summary>
    [Fact]
    public void Dos_valores_que_difieren_despues_del_caracter_512_dan_hashes_distintos()
    {
        var prefijo = new string('x', 600);
        Assert.NotEqual(NaturalKey.Hash(prefijo + "A"), NaturalKey.Hash(prefijo + "B"));
    }
}
