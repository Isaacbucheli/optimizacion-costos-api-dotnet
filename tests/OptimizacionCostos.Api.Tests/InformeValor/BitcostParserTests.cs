using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

public sealed class BitcostParserTests
{
    private static readonly string?[] Cabecera =
    [
        "Tenant", "Nombre Suscripción", "Id Suscripción", "Grupo de Recursos", "Recurso",
        "Centro de Costo", "Categoría", "Subcategoría", "Servicio", "Cantidad", "Unidad",
        "Tarifa", "PVP", "Jerarquía de Fechas - Año", "Jerarquía de Fechas - Mes",
    ];

    private static string?[] Fila(string recurso, string pvp, string anio, string mes, string categoria = "Storage") =>
        ["t-1", "Azure plan", "sub-1", "rg-prod", recurso, "IT", categoria, "Files", "Hot", "1", "1/Hour", "0.01", pvp, anio, mes];

    [Fact]
    public void Lee_una_fila_completa()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([Cabecera, Fila("vm-uno", "12.50", "2026", "Enero")]);
        var r = BitcostParser.Parse(xlsx);
        var fila = Assert.Single(r.Rows);
        Assert.Equal("vm-uno", fila.ResourceName);
        Assert.Equal(12.50m, fila.Pvp);
        Assert.Equal((short)2026, fila.Year);
        Assert.Equal((byte)1, fila.Month);
        Assert.Equal("IT", fila.CostCenter);
    }

    /// <summary>El export real trae el mes con un espacio delante: " Enero".</summary>
    [Fact]
    public void El_mes_tolera_espacios_y_mayusculas()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([Cabecera, Fila("vm-uno", "1", "2026", "  DICIEMBRE ")]);
        Assert.Equal((byte)12, Assert.Single(BitcostParser.Parse(xlsx).Rows).Month);
    }

    [Fact]
    public void El_mes_tambien_acepta_numero()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([Cabecera, Fila("vm-uno", "1", "2026", "7")]);
        Assert.Equal((byte)7, Assert.Single(BitcostParser.Parse(xlsx).Rows).Month);
    }

    [Fact]
    public void Descarta_filas_de_subtotal_sin_recurso_ni_grupo()
    {
        string?[] subtotal = ["", "", "", "", "", "", "", "", "", "", "", "", "999", "2026", "Enero"];
        using var xlsx = XlsxRowReaderTests.BuildXlsx([Cabecera, Fila("vm-uno", "1", "2026", "Enero"), subtotal]);
        var r = BitcostParser.Parse(xlsx);
        Assert.Single(r.Rows);
        Assert.Equal(1, r.RowsSkipped);
    }

    [Fact]
    public void Descarta_filas_sin_periodo_valido()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([Cabecera, Fila("vm-uno", "1", "", "Enero")]);
        var r = BitcostParser.Parse(xlsx);
        Assert.Empty(r.Rows);
        Assert.Equal(1, r.RowsSkipped);
    }

    /// <summary>
    /// El grano de la tabla de hechos incluye categoría, subcategoría, servicio, centro de
    /// costo y unidad: el mismo recurso aparece varias veces en el mismo mes. Medido sobre un
    /// export real: 26.611 filas colapsan a 14.111. Si la clave las funde, se pierde el
    /// desglose por categoría que el cálculo del ahorro necesita.
    /// </summary>
    [Fact]
    public void El_mismo_recurso_en_el_mismo_mes_con_distinta_categoria_son_dos_filas()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([
            Cabecera,
            Fila("vm-uno", "10", "2026", "Enero", "Storage"),
            Fila("vm-uno", "20", "2026", "Enero", "Backup"),
        ]);
        var r = BitcostParser.Parse(xlsx);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(2, r.Rows.Select(x => x.Hash).Distinct().Count());
    }

    /// <summary>Dos filas idénticas se suman en memoria: el índice único no las admitiría.</summary>
    [Fact]
    public void Dos_filas_identicas_se_suman_en_una_sola()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([
            Cabecera,
            Fila("vm-uno", "10", "2026", "Enero"),
            Fila("vm-uno", "5.25", "2026", "Enero"),
        ]);
        var r = BitcostParser.Parse(xlsx);
        Assert.Equal(15.25m, Assert.Single(r.Rows).Pvp);
    }

    [Fact]
    public void El_nombre_de_recurso_se_trunca_pero_el_hash_usa_el_valor_completo()
    {
        var largo = new string('r', 600);
        using var a = XlsxRowReaderTests.BuildXlsx([Cabecera, Fila(largo + "A", "1", "2026", "Enero")]);
        using var b = XlsxRowReaderTests.BuildXlsx([Cabecera, Fila(largo + "B", "1", "2026", "Enero")]);
        var ra = Assert.Single(BitcostParser.Parse(a).Rows);
        var rb = Assert.Single(BitcostParser.Parse(b).Rows);
        Assert.Equal(512, ra.ResourceName!.Length);
        Assert.NotEqual(ra.Hash, rb.Hash);
        Assert.Equal(1, BitcostParser.Parse(XlsxRowReaderTests.BuildXlsx([Cabecera, Fila(largo + "A", "1", "2026", "Enero")])).TruncatedValues);
    }

    [Fact]
    public void Sin_las_columnas_esperadas_lanza_con_mensaje_para_el_usuario()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([["Uno", "Dos"], ["a", "b"]]);
        var ex = Assert.Throws<InvalidOperationException>(() => BitcostParser.Parse(xlsx));
        Assert.Contains("BITCOST", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
