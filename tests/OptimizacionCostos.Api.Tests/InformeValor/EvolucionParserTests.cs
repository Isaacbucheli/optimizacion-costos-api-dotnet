namespace OptimizacionCostos.Api.Tests.InformeValor;

using OptimizacionCostos.Api.Features.InformeValor;

public class EvolucionParserTests
{
    private static readonly string[] FilaAnios =
        ["Jerarquía de Fechas - Año", "", "", "2025", "2025", "2025", "2026", "2026", "2026", "Total"];
    private static readonly string[] FilaMeses =
        ["Jerarquía de Fechas - Mes", "", "", " Noviembre", " Diciembre", "Total", " Enero", " Febrero", "Total", ""];
    private static readonly string[] FilaCabecera =
        ["Categoría", "Subcategoría", "Recurso", "PvP", "PvP", "PvP", "PvP", "PvP", "PvP", "PvP"];

    private static ParseResult<EvolucionRow> Parse(params string[][] datos)
    {
        var filas = new List<string[]> { FilaAnios, FilaMeses, FilaCabecera };
        filas.AddRange(datos);
        using var xlsx = XlsxRowReaderTests.BuildXlsx(filas);
        return EvolucionParser.Parse(xlsx);
    }

    /// <summary>El pivot expande una fila de entrada en una fila por mes con valor.</summary>
    [Fact]
    public void Una_fila_con_dos_meses_produce_dos_filas_de_salida()
    {
        var r = Parse(["Storage", "Disks", "disco-1", "10.5", "", "99", "20.25", "", "99", "99"]);
        Assert.Equal(2, r.Rows.Count);
        var nov = Assert.Single(r.Rows, x => x.PeriodMonth == 11);
        Assert.Equal((short)2025, nov.PeriodYear);
        Assert.Equal(10.5m, nov.Pvp);
        var ene = Assert.Single(r.Rows, x => x.PeriodMonth == 1);
        Assert.Equal((short)2026, ene.PeriodYear);
    }

    /// <summary>Las columnas "Total" (por año y general) fueron la causa del KPI de $26,683:
    /// un subtotal leído como mes. Se descartan por clasificación de la fila de meses.</summary>
    [Fact]
    public void Las_columnas_total_no_producen_filas()
    {
        var r = Parse(["Storage", "Disks", "disco-1", "", "", "555", "", "", "555", "555"]);
        Assert.Empty(r.Rows);
    }

    /// <summary>Subtotales en tres niveles: recurso 'Total', subcategoría 'Total', categoría 'Total'.</summary>
    [Fact]
    public void Las_filas_de_subtotal_no_entran_en_ningun_contador()
    {
        var r = Parse(
            ["Storage", "Disks", "disco-1", "10", "", "", "", "", "", ""],
            ["", "", "Total", "10", "", "", "", "", "", ""],
            ["", "Total", "", "10", "", "", "", "", "", ""],
            ["Total", "", "", "10", "", "", "", "", "", ""]);
        Assert.Single(r.Rows);
        Assert.Equal(0, r.RowsSkipped);
        Assert.Equal(r.Rows.Count + r.RowsSkipped + r.RowsMerged, r.RowsTotal);
    }

    /// <summary>La jerarquía viene solo en la primera fila de cada grupo: fill-down.</summary>
    [Fact]
    public void La_categoria_y_subcategoria_se_arrastran_hacia_abajo()
    {
        var r = Parse(
            ["Azure DNS", "", "zona-1", "1", "", "", "", "", "", ""],
            ["", "", "zona-2", "2", "", "", "", "", "", ""]);
        Assert.All(r.Rows, x => Assert.Equal("Azure DNS", x.Category));
    }

    /// <summary>El pie trae el nombre del cliente y los tenant IDs: se descarta y avisa.</summary>
    [Fact]
    public void El_pie_de_filtros_se_descarta_con_aviso()
    {
        var r = Parse(
            ["Storage", "Disks", "disco-1", "10", "", "", "", "", "", ""],
            ["Filtros aplicados: \nFriendlyName es X\nAzureTenantId es 0001..."]);
        Assert.Single(r.Rows);
        Assert.Contains(r.Warnings, w => w.Contains("pie de filtros", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Las líneas 'Reserved VM Instance, SKU, región, término' son el precio de la
    /// reserva que E2 daba por imposible: van marcadas.</summary>
    [Fact]
    public void Las_lineas_de_reserva_quedan_marcadas()
    {
        var r = Parse(["", "", "Reserved VM Instance, Standard_B16ms, US East 2, 1 Year", "331.71", "", "", "", "", "", ""]);
        Assert.True(Assert.Single(r.Rows).IsReservation);
    }

    /// <summary>Dos celdas que colapsan a la misma clave se suman: el índice único no las admitiría.</summary>
    [Fact]
    public void Claves_duplicadas_se_fusionan_sumando()
    {
        var r = Parse(
            ["Storage", "Disks", "disco-1", "10", "", "", "", "", "", ""],
            ["Storage", "Disks", "disco-1", "5", "", "", "", "", "", ""]);
        Assert.Equal(15m, Assert.Single(r.Rows).Pvp);
        Assert.Equal(1, r.RowsMerged);
    }

    /// <summary>Un archivo que no es el export de evolución tiene que fallar con mensaje claro.</summary>
    [Fact]
    public void Sin_cabecera_de_pivot_lanza_error_de_formato()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([["Recurso", "PVP", "Año", "Mes"], ["vm", "1", "2026", "Enero"]]);
        var ex = Assert.Throws<InvalidOperationException>(() => EvolucionParser.Parse(xlsx));
        Assert.Contains("evolución", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Un pivot con celdas de año combinadas deja la celda vacía en las columnas
    /// siguientes: el año se arrastra.</summary>
    [Fact]
    public void El_anio_se_arrastra_cuando_la_celda_viene_vacia()
    {
        var anios = new[] { "Jerarquía de Fechas - Año", "", "", "2025", "", "", "2026", "", "", "" };
        // Nota: un elemento "[...]" suelto dentro de un inicializador de lista lo parsea el
        // compilador como el comienzo de un indexer-element-initializer ("[...] = valor", la
        // sintaxis de Dictionary), no como un literal de colección: CS1003/CS1525 al compilar.
        // new[] { ... } es inequívoco y trae exactamente los mismos valores.
        var filas = new List<string[]> { anios, FilaMeses, FilaCabecera,
            new[] { "Storage", "Disks", "d1", "1", "2", "", "3", "4", "", "" } };
        using var xlsx = XlsxRowReaderTests.BuildXlsx(filas);
        var r = EvolucionParser.Parse(xlsx);
        Assert.Equal(2, r.Rows.Count(x => x.PeriodYear == 2025));
        Assert.Equal(2, r.Rows.Count(x => x.PeriodYear == 2026));
    }

    /// <summary>PVP no convertible: se cuenta y avisa, nunca se descarta en silencio (D13).</summary>
    [Fact]
    public void Un_pvp_no_convertible_cuenta_como_descartado_con_aviso()
    {
        var r = Parse(["Storage", "Disks", "disco-1", "no-numero", "5", "", "", "", "", ""]);
        Assert.Single(r.Rows);
        Assert.Equal(1, r.RowsSkipped);
        Assert.NotEmpty(r.Warnings);
        Assert.Equal(r.Rows.Count + r.RowsSkipped + r.RowsMerged, r.RowsTotal);
    }
}
