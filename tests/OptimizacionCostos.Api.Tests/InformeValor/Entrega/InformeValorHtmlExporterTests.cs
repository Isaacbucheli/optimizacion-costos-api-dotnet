using System.Text;
using System.Text.Json;
using OptimizacionCostos.Api.Features.InformeValor.Entrega;

namespace OptimizacionCostos.Api.Tests.InformeValor.Entrega;

/// <summary>
/// El exportador del artefacto HTML (F3, F1). Tres cosas se prueban acá porque las tres producen
/// un archivo que miente sin fallar: el escapado dentro del <c>&lt;script&gt;</c>, los nombres del
/// modelo tal cual los espera la capa de dibujo, y el recorte de los bloques económicos.
/// </summary>
public sealed class InformeValorHtmlExporterTests
{
    // ================================================================================
    // El escapado del <script> (F3)
    // ================================================================================

    /// <summary>
    /// El test que pide el plan: un valor de texto con <c>&lt;/script&gt;</c> y <c>&lt;!--</c> no
    /// rompe el artefacto. Los nombres los elige el cliente, así que esto no es un caso rebuscado.
    ///
    /// <para>La prueba no busca la secuencia: extrae el bloque de datos cortando en el PRIMER
    /// <c>&lt;/script&gt;</c> —exactamente como haría el navegador— y exige que lo que quede sea
    /// JSON válido con el nombre intacto. Sin escapar, el corte cae a mitad del JSON y el parseo
    /// falla, que es la forma en que el defecto se manifestaría de verdad.</para>
    /// </summary>
    [Theory]
    [InlineData("Cliente </script><script>alert(1)</script>")]
    [InlineData("Cliente <!-- comentario -->")]
    [InlineData("Cliente </SCRIPT >")]
    [InlineData("Cliente & socios <b>")]
    public void Un_texto_con_secuencias_de_html_no_rompe_el_artefacto(string cliente)
    {
        var html = Exportar(ModeloDePrueba.Crear(cliente), VarianteInforme.Interna);

        var (embedded, _) = DatosDe(html);

        Assert.Equal(cliente, embedded.GetProperty("meta").GetProperty("cliente").GetString());
    }

    /// <summary>Dentro del bloque de datos no puede quedar ni un <c>&lt;</c> crudo: es lo que hace
    /// que el corte de arriba caiga siempre donde tiene que caer, sin importar qué secuencia se le
    /// ocurra a nadie.</summary>
    [Fact]
    public void El_bloque_de_datos_no_lleva_ningun_menor_que_crudo()
    {
        var html = Exportar(
            ModeloDePrueba.Crear("Cliente </script> <!-- --> <>&"), VarianteInforme.Interna);

        var bloque = TextoDelBloqueDeDatos(html);

        Assert.DoesNotContain('<', bloque);
        Assert.DoesNotContain('>', bloque);
        Assert.Contains("\\u003c", bloque, StringComparison.Ordinal);
    }

    /// <summary>U+2028 es válido dentro de una cadena JSON pero el navegador no lee JSON: lee
    /// JavaScript, y hasta ES2019 ese carácter partía la cadena en dos. Puede venir en una celda de
    /// Excel.</summary>
    [Fact]
    public void Los_separadores_de_linea_unicode_viajan_escapados()
    {
        var conSeparador = "Cliente\u2028Sociedad\u2029Anonima";

        var html = Exportar(ModeloDePrueba.Crear(conSeparador), VarianteInforme.Interna);

        var bloque = TextoDelBloqueDeDatos(html);
        Assert.DoesNotContain('\u2028', bloque);
        Assert.DoesNotContain('\u2029', bloque);
        var (embedded, _) = DatosDe(html);
        Assert.Equal(conSeparador, embedded.GetProperty("meta").GetProperty("cliente").GetString());
    }

    /// <summary>D13: la clave de diccionario con espacios y acentos llega intacta. Con la política
    /// global del repo (snake_case también en las claves) el gráfico buscaría una clave que no
    /// existe y dibujaría ceros bajo un título que afirma que hubo ahorro.</summary>
    [Fact]
    public void Las_claves_de_catSerie_llegan_sin_transformar()
    {
        var html = Exportar(ModeloDePrueba.Crear(), VarianteInforme.Interna);

        var (embedded, _) = DatosDe(html);

        var catSerie = embedded.GetProperty("catSerie");
        Assert.True(catSerie.TryGetProperty(ModeloDePrueba.CategoriaConAcentos, out var porMes));
        Assert.Equal(11201m, porMes.GetProperty("2026-01").GetDecimal());
    }

    // ================================================================================
    // Las dos variantes y los seis bloques (F1)
    // ================================================================================

    [Fact]
    public void La_variante_interna_publica_los_seis_bloques_y_no_recorta_nada()
    {
        var html = Exportar(ModeloDePrueba.Crear(), VarianteInforme.Interna, []);

        var (embedded, publicacion) = DatosDe(html);

        Assert.Equal("interna", publicacion.GetProperty("variante").GetString());
        foreach (var b in BloqueEconomicoExtensions.Todos)
            Assert.True(publicacion.GetProperty("bloques").GetProperty(b.Clave()).GetBoolean(),
                $"la variante interna tiene que publicar {b.Clave()}");

        var texto = embedded.GetRawText();
        foreach (var (_, monto) in ModeloDePrueba.Montos)
            Assert.Contains(monto.ToString("0"), texto, StringComparison.Ordinal);
    }

    /// <summary>
    /// El caso peligroso: informe de cliente sin ningún bloque aprobado (el default). Ningún monto
    /// del modelo puede sobrevivir en el archivo — el cliente puede abrir <c>EMBEDDED</c> desde el
    /// navegador, así que no alcanza con no dibujarlo.
    /// </summary>
    [Fact]
    public void La_variante_del_cliente_sin_bloques_aprobados_no_lleva_ningun_monto()
    {
        var html = Exportar(ModeloDePrueba.Crear(), VarianteInforme.Cliente, []);

        var (embedded, publicacion) = DatosDe(html);

        Assert.Equal("cliente", publicacion.GetProperty("variante").GetString());
        foreach (var b in BloqueEconomicoExtensions.Todos)
            Assert.False(publicacion.GetProperty("bloques").GetProperty(b.Clave()).GetBoolean(),
                $"{b.Clave()} nace apagado y nadie lo aprobó");

        var texto = embedded.GetRawText();
        Assert.NotEmpty(ModeloDePrueba.Montos); // que la lista no esté vacía por accidente
        foreach (var (_, monto) in ModeloDePrueba.Montos)
            Assert.DoesNotContain(monto.ToString("0"), texto, StringComparison.Ordinal);
    }

    /// <summary>Un monto suprimido viaja como <c>null</c>, nunca como cero: es la señal que la capa
    /// de dibujo convierte en "No publicado". Un cero ahí le afirma al cliente que no gastó.</summary>
    [Fact]
    public void Un_monto_suprimido_viaja_como_null_y_no_como_cero()
    {
        var html = Exportar(ModeloDePrueba.Crear(), VarianteInforme.Cliente, []);

        var (embedded, _) = DatosDe(html);

        var fact = embedded.GetProperty("fact");
        Assert.Equal(JsonValueKind.Null, fact.GetProperty("total").ValueKind);
        Assert.Equal(JsonValueKind.Null, fact.GetProperty("cargaRet").ValueKind);
        Assert.Equal(JsonValueKind.Null, fact.GetProperty("meses")[0][1].ValueKind);
        Assert.Equal(JsonValueKind.Null, fact.GetProperty("ahorro").GetProperty("dif").ValueKind);
        Assert.Equal(JsonValueKind.Null, embedded.GetProperty("advisor").GetProperty("real").ValueKind);

        // Y lo que NO es monto sigue estando: la sección conserva su relato en conteos.
        Assert.Equal(1, fact.GetProperty("bajasDef").GetInt32());
        Assert.Equal(ModeloDePrueba.CategoriaConAcentos, fact.GetProperty("ahorro").GetProperty("cat").GetString());
        Assert.Equal(8, embedded.GetProperty("advisor").GetProperty("n").GetInt32());
    }

    /// <summary>Aprobar un bloque publica SUS montos y nada más. Es la prueba de que el recorte va
    /// por bloque y no por sección entera.</summary>
    [Theory]
    [InlineData(BloqueEconomico.GastoTotal)]
    [InlineData(BloqueEconomico.SerieMensual)]
    [InlineData(BloqueEconomico.ComposicionServicio)]
    [InlineData(BloqueEconomico.AhorroActivo)]
    [InlineData(BloqueEconomico.CentroCosto)]
    [InlineData(BloqueEconomico.AhorroAdvisor)]
    public void Cada_bloque_aprobado_publica_exactamente_sus_montos(BloqueEconomico bloque)
    {
        var html = Exportar(ModeloDePrueba.Crear(), VarianteInforme.Cliente, [bloque]);

        var (embedded, _) = DatosDe(html);
        var texto = embedded.GetRawText();

        foreach (var (deQuien, monto) in ModeloDePrueba.Montos)
        {
            var esperado = deQuien == bloque.Clave();
            var esta = texto.Contains(monto.ToString("0"), StringComparison.Ordinal);
            Assert.True(esperado == esta,
                $"con {bloque.Clave()} aprobado, el monto {monto} (de {deQuien}) " +
                (esperado ? "tendría que estar y no está" : "no tendría que estar y está"));
        }
    }

    /// <summary>
    /// La descomposición de la variación del consumo (entrega 2d) no la cubre ninguno de los seis
    /// bloques aprobables ni la dibuja ningún renderizador todavía. En la variante del cliente no
    /// viaja, ni siquiera con los seis bloques aprobados: publicar por descuido montos que nadie
    /// aprobó es exactamente lo que la aprobación caso por caso existe para impedir.
    /// </summary>
    [Fact]
    public void La_variacion_del_consumo_no_viaja_en_la_variante_del_cliente()
    {
        var html = Exportar(ModeloDePrueba.Crear(), VarianteInforme.Cliente, BloqueEconomicoExtensions.Todos);

        var (embedded, _) = DatosDe(html);

        Assert.Equal(JsonValueKind.Null, embedded.GetProperty("fact").GetProperty("variacionConsumo").ValueKind);
    }

    // ================================================================================
    // El artefacto como documento
    // ================================================================================

    /// <summary>El informe entregado no lleva la zona de carga: es un documento cerrado, y su capa
    /// de dibujo ya no sabe calcular desde archivos. Ofrecerle al cliente arrastrar Excel encima
    /// sería una invitación a un resultado roto.</summary>
    [Fact]
    public void El_artefacto_sale_sin_la_zona_de_carga_ni_su_enlace()
    {
        var html = Texto(ModeloDePrueba.Crear(), VarianteInforme.Cliente);

        Assert.DoesNotContain("<section id=\"carga\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"lnk-carga\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"drop\"", html, StringComparison.Ordinal);
        // Y las secciones del informe siguen ahí.
        Assert.Contains("id=\"body-eficiencia\"", html, StringComparison.Ordinal);
        Assert.Contains("if(EMBEDDED){", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Las_dos_variantes_salen_con_nombre_de_archivo_distinto()
    {
        var modelo = ModeloDePrueba.Crear("Cliente / Prueba S.A.");

        var interna = InformeValorHtmlExporter.Exportar(modelo, VarianteInforme.Interna);
        var cliente = InformeValorHtmlExporter.Exportar(modelo, VarianteInforme.Cliente, []);

        Assert.EndsWith("-interna.html", interna.FileName, StringComparison.Ordinal);
        Assert.EndsWith("-cliente.html", cliente.FileName, StringComparison.Ordinal);
        Assert.NotEqual(interna.FileName, cliente.FileName);
        // El nombre sale de un texto que escribe el consultor: nada de barras ni puntos sueltos.
        Assert.DoesNotContain('/', interna.FileName);
        Assert.Equal(1, interna.FileName.Count(c => c == '.'));
    }

    /// <summary>Lo que se archiva es lo que el archivo hace, no lo que se pidió: la interna publica
    /// los seis aunque quien llame no pase ninguno.</summary>
    [Fact]
    public void El_artefacto_declara_los_bloques_que_realmente_publica()
    {
        var interna = InformeValorHtmlExporter.Exportar(ModeloDePrueba.Crear(), VarianteInforme.Interna, []);
        var cliente = InformeValorHtmlExporter.Exportar(
            ModeloDePrueba.Crear(), VarianteInforme.Cliente, [BloqueEconomico.CentroCosto, BloqueEconomico.CentroCosto]);

        Assert.Equal(6, interna.BloquesPublicados.Count);
        Assert.Equal([BloqueEconomico.CentroCosto], cliente.BloquesPublicados);
    }

    /// <summary>La huella de la plantilla es estable entre corridas (si no, cada entrega quedaría
    /// archivada con una versión distinta y la columna no serviría para nada) y no vacía.</summary>
    [Fact]
    public void La_huella_de_la_plantilla_es_estable()
    {
        var a = InformeValorHtmlExporter.PlantillaVersion;
        var b = InformeValorHtmlExporter.Exportar(ModeloDePrueba.Crear(), VarianteInforme.Interna).PlantillaVersion;

        Assert.Equal(16, a.Length);
        Assert.Equal(a, b);
    }

    // ================================================================================
    // Helpers
    // ================================================================================

    private static string Texto(
        OptimizacionCostos.Api.Features.InformeValor.Calculo.ModeloInformeValor modelo,
        VarianteInforme variante,
        IReadOnlyCollection<BloqueEconomico>? bloques = null) =>
        Encoding.UTF8.GetString(InformeValorHtmlExporter.Exportar(modelo, variante, bloques).Contenido);

    private static string Exportar(
        OptimizacionCostos.Api.Features.InformeValor.Calculo.ModeloInformeValor modelo,
        VarianteInforme variante,
        IReadOnlyCollection<BloqueEconomico>? bloques = null) => Texto(modelo, variante, bloques);

    /// <summary>El contenido del bloque de datos, cortado en el PRIMER <c>&lt;/script&gt;</c>, que
    /// es donde lo cortaría el navegador. Si el escapado falla, este corte cae a mitad del JSON.</summary>
    private static string TextoDelBloqueDeDatos(string html)
    {
        const string abre = "<script id=\"data\">";
        var i = html.IndexOf(abre, StringComparison.Ordinal);
        Assert.True(i >= 0, "el artefacto no tiene bloque de datos");
        i += abre.Length;
        var j = html.IndexOf("</script>", i, StringComparison.Ordinal);
        Assert.True(j > i, "el bloque de datos quedó sin cerrar");
        return html[i..j];
    }

    private static (JsonElement Embedded, JsonElement Publicacion) DatosDe(string html)
    {
        var bloque = TextoDelBloqueDeDatos(html);
        const string p1 = "var EMBEDDED=";
        const string p2 = ";var PUBLICACION=";
        var i = bloque.IndexOf(p1, StringComparison.Ordinal) + p1.Length;
        var j = bloque.LastIndexOf(p2, StringComparison.Ordinal);
        Assert.True(j > i, "el bloque de datos no tiene las dos variables");

        var embedded = JsonDocument.Parse(bloque[i..j]).RootElement;
        var publicacion = JsonDocument.Parse(bloque[(j + p2.Length)..].TrimEnd(';')).RootElement;
        return (embedded, publicacion);
    }
}
