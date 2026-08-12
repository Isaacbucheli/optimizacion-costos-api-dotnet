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

    private static string?[] Fila(
        string recurso, string pvp, string anio, string mes, string categoria = "Storage",
        string cantidad = "1", string tarifa = "0.01") =>
        ["t-1", "Azure plan", "sub-1", "rg-prod", recurso, "IT", categoria, "Files", "Hot", cantidad, "1/Hour", tarifa, pvp, anio, mes];

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
        Assert.Equal(1, r.RowsMerged);
    }

    /// <summary>
    /// La regla que tiene que cerrarle al consultor: total = procesadas + descartadas +
    /// fusionadas. Antes de este fix una fusión no se contaba en ningún lado (ver el comentario
    /// de la clase): con el archivo real de 26.611 filas eso daba rows_total: 26611,
    /// rows_processed: 14111, rows_skipped: 0 sin ninguna explicación de los 12.500 que faltaban.
    /// </summary>
    [Fact]
    public void La_aritmetica_de_filas_fusionadas_le_cierra_al_consultor()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([
            Cabecera,
            Fila("vm-uno", "10", "2026", "Enero"),
            Fila("vm-uno", "5", "2026", "Enero"),
            Fila("vm-uno", "1", "2026", "Enero"),
        ]);
        var r = BitcostParser.Parse(xlsx);

        Assert.Equal(3, r.RowsTotal);
        Assert.Single(r.Rows);
        Assert.Equal(0, r.RowsSkipped);
        Assert.Equal(2, r.RowsMerged);
        Assert.Equal(r.RowsTotal, r.Rows.Count + r.RowsSkipped + r.RowsMerged);
        Assert.Contains(r.Warnings, w => w.Contains("fusion", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Quantity es aditiva igual que Pvp: dos filas fusionadas son el mismo recurso repetido, así
    /// que sus cantidades también se suman. Antes de este fix la fila fusionada se quedaba con
    /// la Quantity de la primera nomás, junto a un Pvp que sí era la suma de las N filas: un
    /// importe de N filas al lado de la cantidad de una sola.
    /// </summary>
    [Fact]
    public void Al_fusionar_la_cantidad_tambien_se_suma()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([
            Cabecera,
            Fila("vm-uno", "10", "2026", "Enero", cantidad: "2"),
            Fila("vm-uno", "5", "2026", "Enero", cantidad: "3"),
        ]);
        var f = Assert.Single(BitcostParser.Parse(xlsx).Rows);
        Assert.Equal(5m, f.Quantity);
    }

    /// <summary>
    /// Rate es un precio UNITARIO, no algo que se acumule: sumar dos tarifas no da un número con
    /// significado de negocio. Se conserva solo si todas las filas fusionadas coinciden en el
    /// mismo valor; en cuanto una difiere, no hay una sola tarifa que describa la fila resultante
    /// y queda en null antes que inventar un número.
    /// </summary>
    [Fact]
    public void Al_fusionar_la_tarifa_se_conserva_solo_si_coincide_en_todas()
    {
        using var igual = XlsxRowReaderTests.BuildXlsx([
            Cabecera,
            Fila("vm-uno", "10", "2026", "Enero", tarifa: "0.05"),
            Fila("vm-uno", "5", "2026", "Enero", tarifa: "0.05"),
        ]);
        Assert.Equal(0.05m, Assert.Single(BitcostParser.Parse(igual).Rows).Rate);

        using var distinta = XlsxRowReaderTests.BuildXlsx([
            Cabecera,
            Fila("vm-uno", "10", "2026", "Enero", tarifa: "0.05"),
            Fila("vm-uno", "5", "2026", "Enero", tarifa: "0.09"),
        ]);
        Assert.Null(Assert.Single(BitcostParser.Parse(distinta).Rows).Rate);
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
