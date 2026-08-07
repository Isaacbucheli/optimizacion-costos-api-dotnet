using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

public sealed class CasosParserTests
{
    private static readonly string?[] Cabecera =
    [
        "Caso", "Fecha de Registro", "Estado", "SLA horas", "Duración",
        "Cumple SLA", "Categoría", "Subcategoría", "Horario",
    ];

    private static string?[] Fila(string caso, string fecha, string dur, string cumple) =>
        [caso, fecha, "Cerrado", "4", dur, cumple, "CÓMPUTO", "Solicitud de cambio", "Hábil"];

    [Fact]
    public void Lee_una_fila_completa()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([Cabecera, Fila("RF-1", "2026-01-15", "2.5", "SI")]);
        var f = Assert.Single(CasosParser.Parse(xlsx).Rows);
        Assert.Equal("RF-1", f.Caso);
        Assert.Equal(new DateOnly(2026, 1, 15), f.FechaRegistro);
        Assert.Equal(2.5m, f.DuracionCruda);
        Assert.Equal("SI", f.Cumple);
        Assert.Equal("CÓMPUTO", f.Categoria);
    }

    /// <summary>
    /// La duración se guarda tal cual viene. La heurística de días contra horas se resuelve
    /// sobre el conjunto acumulado en la entrega 2, no por archivo: convertir acá mezclaría
    /// de forma irreversible dos archivos con unidades distintas.
    /// </summary>
    [Fact]
    public void La_duracion_se_guarda_sin_convertir()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([Cabecera, Fila("RF-1", "2026-01-15", "1.5", "SI")]);
        Assert.Equal(1.5m, Assert.Single(CasosParser.Parse(xlsx).Rows).DuracionCruda);
    }

    [Fact]
    public void Acepta_fecha_en_serial_de_Excel()
    {
        // 46023 = 2026-01-15 en el calendario 1900 de Excel.
        using var xlsx = XlsxRowReaderTests.BuildXlsx([Cabecera, Fila("RF-1", "46037", "1", "SI")]);
        var f = Assert.Single(CasosParser.Parse(xlsx).Rows);
        Assert.NotNull(f.FechaRegistro);
        Assert.Equal(2026, f.FechaRegistro!.Value.Year);
    }

    [Fact]
    public void Una_fecha_ilegible_deja_aviso_y_no_descarta_la_fila()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([Cabecera, Fila("RF-1", "ayer", "1", "SI")]);
        var r = CasosParser.Parse(xlsx);
        var f = Assert.Single(r.Rows);
        Assert.Null(f.FechaRegistro);
        Assert.Contains(r.Warnings, w => w.Contains("fecha", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Descarta_filas_sin_caso_ni_estado()
    {
        string?[] vacia = ["", "", "", "", "", "", "", "", ""];
        using var xlsx = XlsxRowReaderTests.BuildXlsx([Cabecera, Fila("RF-1", "2026-01-15", "1", "SI"), vacia]);
        var r = CasosParser.Parse(xlsx);
        Assert.Single(r.Rows);
        Assert.Equal(1, r.RowsSkipped);
    }

    [Fact]
    public void Dos_casos_distintos_dan_hashes_distintos()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([
            Cabecera, Fila("RF-1", "2026-01-15", "1", "SI"), Fila("RF-2", "2026-01-15", "1", "SI"),
        ]);
        var r = CasosParser.Parse(xlsx);
        Assert.Equal(2, r.Rows.Select(x => x.Hash).Distinct().Count());
    }

    [Fact]
    public void Sin_las_columnas_esperadas_lanza_con_mensaje_para_el_usuario()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([["Uno", "Dos"], ["a", "b"]]);
        var ex = Assert.Throws<InvalidOperationException>(() => CasosParser.Parse(xlsx));
        Assert.Contains("mesa de servicio", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
