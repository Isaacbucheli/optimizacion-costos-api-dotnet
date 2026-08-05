using OptimizacionCostos.Api.Data;
using OptimizacionCostos.Api.Features.AlertCatalog;
using OptimizacionCostos.Api.Features.Boletin;
using OptimizacionCostos.Api.Features.PolicyCatalog;

namespace OptimizacionCostos.Api.Tests.Data;

/// <summary>
/// Regresión del ZAP del 2026-08-03: el escáner reportó "Format String Error" (CWE-134) en los 4
/// campos de policy_catalog cuyo ancho de columna era menor que el payload, y en ninguno de los que
/// sí cabían. La causa real era el error 8152 de SQL Server ("String or binary data would be
/// truncated") saliendo como conexión cortada. Estas pruebas fijan que el largo se valide antes.
/// </summary>
public sealed class ColumnLimitsTests
{
    // Los dos payloads textuales que mandó ZAP, reconstruidos igual que en el informe.
    private static readonly string PayloadLargo =
        "ZAP " + string.Concat(Enumerable.Range(1, 20).Select(i => $"%{i}!s"))
               + string.Concat(Enumerable.Range(21, 20).Select(i => $"%{i}!n")) + "\n";

    private static readonly string PayloadCorto =
        "ZAP" + string.Concat(Enumerable.Repeat("%n%s", 20)) + "\n";

    [Fact]
    public void Los_payloads_del_informe_tienen_el_largo_esperado()
    {
        Assert.Equal(196, PayloadLargo.Length);
        Assert.Equal(84, PayloadCorto.Length);
    }

    [Theory]
    // Exactamente las 4 columnas que ZAP alertó, con el payload que recibió cada una.
    [InlineData("category", 160)]
    [InlineData("policy_type", 80)]
    [InlineData("recommended_effect", 60)]
    [InlineData("mode", 40)]
    public void Payload_de_ZAP_en_columna_angosta_es_rechazado(string column, int max)
    {
        var payload = column == "category" ? PayloadLargo : PayloadCorto;
        Assert.True(payload.Length > max, "el payload debe exceder la columna para que el caso sirva");

        var error = ColumnLimits.FirstViolation(
            new Dictionary<string, object?> { [column] = payload }, PolicyColumns.MaxLengths);

        Assert.Equal($"El campo '{column}' excede el máximo de {max} caracteres", error);
    }

    [Theory]
    // Las columnas que aceptaron el payload sin alertar: siguen aceptándolo.
    [InlineData("name")]
    [InlineData("key_parameters")]
    [InlineData("recommended_scope")]
    [InlineData("official_source")]
    public void Payload_de_ZAP_en_columna_ancha_pasa(string column)
    {
        var error = ColumnLimits.FirstViolation(
            new Dictionary<string, object?> { [column] = PayloadLargo }, PolicyColumns.MaxLengths);
        Assert.Null(error);
    }

    [Fact]
    public void Columnas_nvarchar_max_no_tienen_limite()
    {
        // description/objective/rollout/risk/azure_cli/powershell/script_notes son NVARCHAR(MAX):
        // quedan fuera del mapa a propósito, y es su ausencia lo que las deja pasar sin chequeo.
        var enorme = new string('x', 100_000);
        foreach (var col in new[] { "description", "objective", "rollout", "risk", "azure_cli", "powershell", "script_notes" })
        {
            Assert.DoesNotContain(col, PolicyColumns.MaxLengths.Keys);
            Assert.Null(ColumnLimits.FirstViolation(
                new Dictionary<string, object?> { [col] = enorme }, PolicyColumns.MaxLengths));
        }
    }

    [Fact]
    public void Valor_exactamente_del_ancho_de_la_columna_pasa()
    {
        foreach (var (col, max) in PolicyColumns.MaxLengths)
            Assert.Null(ColumnLimits.FirstViolation(
                new Dictionary<string, object?> { [col] = new string('x', max) }, PolicyColumns.MaxLengths));
    }

    [Fact]
    public void Un_caracter_mas_que_el_ancho_es_rechazado()
    {
        foreach (var (col, max) in PolicyColumns.MaxLengths)
            Assert.NotNull(ColumnLimits.FirstViolation(
                new Dictionary<string, object?> { [col] = new string('x', max + 1) }, PolicyColumns.MaxLengths));
    }

    [Fact]
    public void Con_varios_campos_excedidos_el_mensaje_es_siempre_el_mismo()
    {
        // El recorrido va por orden alfabético y no por el del diccionario. Sin eso, dos bodies con
        // los mismos campos excedidos podían devolver mensajes distintos según el orden de inserción,
        // y un reporte de ese 400 no se podía reproducir. 'category' < 'mode' < 'policy_type'.
        var enOrden = new Dictionary<string, object?>
        {
            ["category"] = new string('x', 200),
            ["mode"] = new string('x', 200),
            ["policy_type"] = new string('x', 200),
        };
        var alReves = new Dictionary<string, object?>
        {
            ["policy_type"] = new string('x', 200),
            ["mode"] = new string('x', 200),
            ["category"] = new string('x', 200),
        };

        var a = ColumnLimits.FirstViolation(enOrden, PolicyColumns.MaxLengths);
        var b = ColumnLimits.FirstViolation(alReves, PolicyColumns.MaxLengths);

        Assert.Equal(a, b);
        Assert.Contains("'category'", a);
    }

    [Fact]
    public void Valores_no_string_se_ignoran()
    {
        // policy_number es INT y is_active es BIT: no son texto, no tienen largo que validar.
        var fields = new Dictionary<string, object?>
        {
            ["policy_number"] = 7,
            ["is_active"] = true,
            ["mode"] = null,
        };
        Assert.Null(ColumnLimits.FirstViolation(fields, PolicyColumns.MaxLengths));
    }

    [Fact]
    public void Los_mapas_de_los_otros_catalogos_tambien_acotan_sus_columnas()
    {
        // El mismo patrón BuildFields vive en alert-catalog y en los catálogos del Boletín, así que
        // el próximo escaneo los va a fuzzear igual. Se fija que sus mapas existan y muerdan.
        Assert.Equal(300, AlertColumns.AlertMaxLengths["name"]);
        Assert.Equal(40, AlertColumns.AlertMaxLengths["severity"]);
        Assert.Equal(200, AlertColumns.KqlMaxLengths["name"]);
        Assert.Equal(20, LifecycleColumns.MaxLengths["categoria"]);
        Assert.Equal(64, MigracionColumns.MaxLengths["clave"]);

        Assert.NotNull(ColumnLimits.FirstViolation(
            new Dictionary<string, object?> { ["severity"] = PayloadCorto }, AlertColumns.AlertMaxLengths));
        Assert.NotNull(ColumnLimits.FirstViolation(
            new Dictionary<string, object?> { ["categoria"] = PayloadCorto }, LifecycleColumns.MaxLengths));
        Assert.NotNull(ColumnLimits.FirstViolation(
            new Dictionary<string, object?> { ["clave"] = PayloadCorto }, MigracionColumns.MaxLengths));
    }

    [Fact]
    public void Todas_las_columnas_acotadas_del_mapa_estan_en_la_whitelist()
    {
        // Si alguien renombra una columna en la whitelist y olvida el mapa, el límite deja de
        // aplicarse en silencio y vuelve el 8152. Esto lo caza en build.
        foreach (var col in PolicyColumns.MaxLengths.Keys)
            Assert.Contains(col, PolicyColumns.Policy);
        foreach (var col in AlertColumns.AlertMaxLengths.Keys)
            Assert.Contains(col, AlertColumns.Alert);
        foreach (var col in AlertColumns.KqlMaxLengths.Keys)
            Assert.Contains(col, AlertColumns.Kql);
        foreach (var col in LifecycleColumns.MaxLengths.Keys)
            Assert.Contains(col, LifecycleColumns.Editable);
        foreach (var col in MigracionColumns.MaxLengths.Keys)
            Assert.Contains(col, MigracionColumns.Editable);
    }
}
