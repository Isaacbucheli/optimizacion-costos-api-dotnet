using System.Text.Json;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// La trampa más cara del port (D13): el repo serializa toda respuesta de controller en
/// snake_case, INCLUIDAS las claves de diccionario (<c>Program.cs</c>: <c>PropertyNamingPolicy</c>
/// y <c>DictionaryKeyPolicy</c> en <c>SnakeCaseLower</c>). Estos tests fijan que el exportador de
/// este módulo usa sus propias opciones y por lo tanto no hereda ese defecto.
/// </summary>
public sealed class InformeValorJsonOptionsTests
{
    /// <summary>El test que pide explícitamente el plan: una categoría con espacios y acentos
    /// sobrevive intacta como clave de diccionario.</summary>
    [Fact]
    public void Una_clave_de_diccionario_con_espacios_y_acentos_sobrevive_intacta()
    {
        var catSerie = new Dictionary<string, decimal> { ["Máquinas Virtuales y Almacenamiento"] = 1234.5m };

        var json = JsonSerializer.Serialize(catSerie, InformeValorJsonOptions.Instance);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("Máquinas Virtuales y Almacenamiento", out var valor));
        Assert.Equal(1234.5m, valor.GetDecimal());
    }

    /// <summary>
    /// Prueba de contraste: confirma que la política GLOBAL del repo (la que Program.cs aplica a
    /// toda respuesta de controller) sí rompería la misma clave, para que el test de arriba no
    /// parezca una comprobación redundante de un comportamiento que ya sería seguro por defecto.
    /// </summary>
    [Fact]
    public void La_politica_global_del_repo_si_transforma_esa_misma_clave()
    {
        var opcionesGlobalesDelRepo = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        var catSerie = new Dictionary<string, decimal> { ["Máquinas Virtuales"] = 1m };

        var json = JsonSerializer.Serialize(catSerie, opcionesGlobalesDelRepo);

        Assert.DoesNotContain("Máquinas Virtuales", json, StringComparison.Ordinal);
    }

    /// <summary>Los nombres de propiedad del modelo son los que declara [JsonPropertyName], no
    /// una inferencia a partir del nombre de C#: "Cliente" -> "cliente" es intencional (coincide
    /// con lo que ya inferiría camelCase), pero no debe convertirse a snake_case.</summary>
    [Fact]
    public void Los_nombres_de_propiedad_del_modelo_no_pasan_por_snake_case()
    {
        var meta = new InformeValorMeta("Cliente Demo", "Enero-Diciembre 2026", "2026-12-31");

        var json = JsonSerializer.Serialize(meta, InformeValorJsonOptions.Instance);

        Assert.Contains("\"cliente\":\"Cliente Demo\"", json, StringComparison.Ordinal);
        Assert.Contains("\"periodo\":", json, StringComparison.Ordinal);
        Assert.Contains("\"corte\":", json, StringComparison.Ordinal);
    }

    /// <summary>El modelo completo serializa con las siete claves de nivel superior que espera
    /// <c>render()</c> (<c>D.meta/.tickets/.fact/.rbac/.advisor/.matriz/.catSerie</c>), ni una
    /// más ni una menos, y los bloques ausentes viajan como <c>null</c> JSON real, no un objeto
    /// vacío que simule ausencia (render() distingue "sin insumo" con <c>if(!t)</c>).</summary>
    [Fact]
    public void El_modelo_completo_serializa_con_las_siete_claves_de_D()
    {
        var modelo = new ModeloInformeValor(
            new InformeValorMeta("Cliente", "2026", "2026-12-31"),
            Operacion: null, Consumo: null, Seguridad: null, Postura: null, Roadmap: null, CatSerie: null);

        var json = JsonSerializer.Serialize(modelo, InformeValorJsonOptions.Instance);

        using var doc = JsonDocument.Parse(json);
        var claves = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(
            new HashSet<string> { "meta", "tickets", "fact", "rbac", "advisor", "matriz", "catSerie" },
            claves);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("tickets").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("catSerie").ValueKind);
    }

    /// <summary>
    /// catSerie es un diccionario DENTRO de otro diccionario (categoría -> mes -> monto): las dos
    /// capas de claves tienen que sobrevivir, no solo la de afuera.
    /// </summary>
    [Fact]
    public void CatSerie_preserva_las_claves_en_sus_dos_niveles()
    {
        var catSerie = new Dictionary<string, IReadOnlyDictionary<string, decimal>>
        {
            ["Redes y Conectividad"] = new Dictionary<string, decimal> { ["2026-01"] = 500m, ["2026-02"] = 480.5m },
        };
        var modelo = new ModeloInformeValor(
            new InformeValorMeta("Cliente", "2026", "2026-12-31"), null, null, null, null, null, catSerie);

        var json = JsonSerializer.Serialize(modelo, InformeValorJsonOptions.Instance);

        using var doc = JsonDocument.Parse(json);
        var serie = doc.RootElement.GetProperty("catSerie").GetProperty("Redes y Conectividad");
        Assert.Equal(500m, serie.GetProperty("2026-01").GetDecimal());
        Assert.Equal(480.5m, serie.GetProperty("2026-02").GetDecimal());
    }
}
