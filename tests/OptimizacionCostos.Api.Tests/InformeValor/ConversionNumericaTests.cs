using System.Globalization;
using OptimizacionCostos.Api.Features.InformeValor;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// Conversión numérica unificada (InsumoCellUtils.TryDecimal), ejercitada a través de los dos
/// parsers que la usan. El método es internal: se prueba por el efecto observable en PVP
/// (BitcostParser, campo obligatorio, el vehículo más simple para fijar un valor por fila) y en
/// SLA/Duración (CasosParser, campos opcionales). Cubre D13 del plan de la entrega 2b: separador
/// de miles, símbolo de moneda, paréntesis contable negativo, signo al final, espacios finos,
/// notación científica y celda vacía contra cero.
/// </summary>
public sealed class ConversionNumericaTests
{
    private static readonly string?[] CabeceraBitcost =
    [
        "Tenant", "Nombre Suscripción", "Id Suscripción", "Grupo de Recursos", "Recurso",
        "Centro de Costo", "Categoría", "Subcategoría", "Servicio", "Cantidad", "Unidad",
        "Tarifa", "PVP", "Jerarquía de Fechas - Año", "Jerarquía de Fechas - Mes",
    ];

    private static string?[] FilaBitcost(string pvp, string cantidad = "1", string tarifa = "0.01") =>
        ["t-1", "Azure plan", "sub-1", "rg-prod", "vm-uno", "IT", "Storage", "Files", "Hot",
         cantidad, "1/Hour", tarifa, pvp, "2026", "Enero"];

    private static decimal Pvp(string raw) =>
        Assert.Single(BitcostParser.Parse(XlsxRowReaderTests.BuildXlsx([CabeceraBitcost, FilaBitcost(raw)])).Rows).Pvp;

    [Theory]
    // Miles con coma, decimal con punto (convención en inglés) y símbolo de moneda.
    [InlineData("$1,234.56", "1234.56")]
    [InlineData("USD 1,234.56", "1234.56")]
    [InlineData("1,234.56", "1234.56")]
    // Miles con punto, decimal con coma (convención en español).
    [InlineData("1.234,56", "1234.56")]
    [InlineData("$1.234,56", "1234.56")]
    // Un solo separador: miles sin decimal, según las dos convenciones.
    [InlineData("1,234", "1234")]
    [InlineData("1.234", "1234")]
    // Un solo separador: decimal sin miles, según las dos convenciones.
    [InlineData("12,5", "12.5")]
    [InlineData("12.5", "12.5")]
    // Un cero adelante no se confunde con agrupación de miles (D13: 0,024 no es 24).
    [InlineData("0,024", "0.024")]
    [InlineData("0.024", "0.024")]
    // Espacio (incluido el fino, separador de miles de algunos exports) se limpia igual que la
    // moneda, sin perder el separador decimal real.
    [InlineData("1 234,56", "1234.56")]
    [InlineData("1 234,56", "1234.56")]
    [InlineData("12 345", "12345")]
    // Notación científica: no se puede tocar la 'E' sin convertir un centavo en 1.6 dólares.
    [InlineData("1.6E-05", "0.000016")]
    [InlineData("1,6E-05", "0.000016")]
    // Celda con solo un signo o separador no es un número: se cubre en un test aparte porque el
    // resultado esperado ahí es "no convierte", no un valor.
    public void Formatos_tolerados_se_convierten_al_valor_correcto(string raw, string esperado)
    {
        Assert.Equal(decimal.Parse(esperado, CultureInfo.InvariantCulture), Pvp(raw));
    }

    [Theory]
    // Paréntesis contable: negativo, al revés que la plantilla JS (que lo vuelve positivo a
    // propósito de un defecto que este módulo corrige).
    [InlineData("(1234)", "-1234")]
    [InlineData("(1234.56)", "-1234.56")]
    [InlineData("($1,234.56)", "-1234.56")]
    // Signo menos adelante (el caso común) y atrás (notación de algunos ERP).
    [InlineData("-1234.56", "-1234.56")]
    [InlineData("1234.56-", "-1234.56")]
    [InlineData("-1,234.56", "-1234.56")]
    public void Signo_negativo_se_reconoce_adelante_atras_y_en_parentesis(string raw, string esperado)
    {
        Assert.Equal(decimal.Parse(esperado, CultureInfo.InvariantCulture), Pvp(raw));
    }

    /// <summary>
    /// Celda vacía no es cero: es la ausencia de un valor. Antes de este fix el criterio era el
    /// mismo (decimal.TryParse ya fallaba con string vacío), pero queda fijado explícitamente
    /// porque el resto de la conversión cambió por completo.
    /// </summary>
    [Fact]
    public void Celda_vacia_no_convierte_y_no_es_cero()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([CabeceraBitcost, FilaBitcost("")]);
        var r = BitcostParser.Parse(xlsx);
        Assert.Empty(r.Rows);
        Assert.Equal(1, r.RowsSkipped);
        // Sin PVP escrito no es un defecto de conversión: no debe aparecer el aviso específico.
        Assert.DoesNotContain(r.Warnings, w => w.Contains("PVP no se pudo convertir", StringComparison.Ordinal));
    }

    /// <summary>Cero explícito sí convierte, distinto de la celda vacía.</summary>
    [Fact]
    public void Cero_explicito_convierte_a_cero()
    {
        Assert.Equal(0m, Pvp("0"));
    }

    /// <summary>
    /// Lo que no se puede convertir queda CONTADO y AVISADO, nunca descartado en silencio (el
    /// criterio de fondo del módulo: ver el comentario de InsumoCellUtils.TryDecimal). La fila se
    /// descarta (PVP es obligatorio) pero el aviso dice específicamente que fue el PVP, no un
    /// "skipped" genérico indistinguible de una fila de subtotal sin recurso.
    /// </summary>
    [Fact]
    public void Pvp_no_convertible_se_cuenta_y_se_avisa_especificamente()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([CabeceraBitcost, FilaBitcost("no-es-un-numero")]);
        var r = BitcostParser.Parse(xlsx);
        Assert.Empty(r.Rows);
        Assert.Equal(1, r.RowsSkipped);
        Assert.Contains(r.Warnings, w => w.Contains("PVP no se pudo convertir", StringComparison.Ordinal));
    }

    /// <summary>
    /// Cantidad y Tarifa son opcionales: antes de este fix una celda no convertible se volvía
    /// null en silencio, indistinguible de una celda vacía. Ahora se cuenta y se avisa, sin
    /// descartar la fila (a diferencia del PVP, estos dos no bloquean la fila).
    /// </summary>
    [Fact]
    public void Cantidad_y_tarifa_no_convertibles_se_cuentan_sin_descartar_la_fila()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx(
            [CabeceraBitcost, FilaBitcost("10", cantidad: "n/a", tarifa: "n/a")]);
        var r = BitcostParser.Parse(xlsx);
        var fila = Assert.Single(r.Rows);
        Assert.Equal(0, r.RowsSkipped);
        Assert.Null(fila.Quantity);
        Assert.Null(fila.Rate);
        Assert.Contains(r.Warnings, w => w.Contains("Cantidad", StringComparison.Ordinal));
        Assert.Contains(r.Warnings, w => w.Contains("Tarifa", StringComparison.Ordinal));
    }

    // ---------- CasosParser: SLA horas y Duración, los dos campos numéricos opcionales ----------

    private static readonly string?[] CabeceraCasos =
        ["Caso", "Fecha de Registro", "Estado", "SLA horas", "Duración",
         "Cumple SLA", "Categoría", "Subcategoría", "Horario"];

    private static string?[] FilaCasos(string sla, string duracion) =>
        ["RF-1", "2026-01-15", "Cerrado", sla, duracion, "SI", "CÓMPUTO", "Solicitud de cambio", "Hábil"];

    [Fact]
    public void Sla_con_separador_de_miles_convierte_igual_que_pvp()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([CabeceraCasos, FilaCasos("1,234.5", "1")]);
        var f = Assert.Single(CasosParser.Parse(xlsx).Rows);
        Assert.Equal(1234.5m, f.SlaHoras);
    }

    [Fact]
    public void Sla_y_duracion_no_convertibles_se_cuentan_sin_descartar_el_caso()
    {
        using var xlsx = XlsxRowReaderTests.BuildXlsx([CabeceraCasos, FilaCasos("no-numero", "tampoco")]);
        var r = CasosParser.Parse(xlsx);
        var f = Assert.Single(r.Rows);
        Assert.Equal(0, r.RowsSkipped);
        Assert.Null(f.SlaHoras);
        Assert.Null(f.DuracionCruda);
        Assert.Contains(r.Warnings, w => w.Contains("SLA", StringComparison.Ordinal));
        Assert.Contains(r.Warnings, w => w.Contains("Duración", StringComparison.Ordinal));
    }
}
