using System.Text.RegularExpressions;
using OptimizacionCostos.Api.Features.InformeValor.Entrega;

namespace OptimizacionCostos.Api.Tests.InformeValor.Entrega;

/// <summary>
/// Audita la capa de dibujo de la plantilla embebida: <c>render()</c> y las tablas interactivas
/// que dependen de él (F2 de la entrega 3).
///
/// <para><b>Por qué un test de texto y no de comportamiento.</b> El JavaScript de la plantilla no
/// se ejecuta en esta suite. Lo que sí se puede fijar, y es lo que rompía, es el CONTRATO DE
/// NOMBRES: <c>render()</c> leía tres nombres que el modelo nuevo ya no publica —<c>tickets.si</c>,
/// <c>tickets.no</c>, <c>fact.cargaAcum</c>— y leía <c>savLineas</c> por posición cuando D7 lo
/// convirtió en objeto con nombre. Ninguno de los cuatro falla: producen <c>undefined</c> impreso
/// en el informe de un cliente, o peor, un veredicto por fila calculado con una regla distinta de
/// la que produjo el total.</para>
///
/// <para>El barrido se limita a la capa de dibujo a propósito. Más arriba en el mismo archivo
/// siguen las funciones <c>calcXxx</c> del generador manual, que SÍ producen los nombres viejos:
/// son código muerto en este camino (el artefacto arranca por <c>if(EMBEDDED)</c>) y buscarlos en
/// todo el archivo daría falsos positivos.</para>
/// </summary>
public sealed class PlantillaCapaDeDibujoTests
{
    /// <summary>Desde <c>function render(){</c> hasta el encabezado de la sección de carga de
    /// archivos: cubre <c>render()</c>, <c>revisaGate</c>, <c>chocaPeriodos</c> y las tres tablas
    /// interactivas.</summary>
    private static string CapaDeDibujo()
    {
        var t = InformeValorHtmlExporter.Plantilla;
        var ini = t.IndexOf("function render(){", StringComparison.Ordinal);
        Assert.True(ini > 0, "no se encontró render() en la plantilla embebida");
        var fin = t.IndexOf("7. CARGA DE ARCHIVOS", ini, StringComparison.Ordinal);
        Assert.True(fin > ini, "no se encontró el final de la capa de dibujo");
        return t[ini..fin];
    }

    /// <summary>
    /// D2: <c>si</c>/<c>no</c> no se renombraron, se partieron en tres estados. Un
    /// <c>t.si</c> sobreviviente imprime <c>undefined</c> en el KPI de portada.
    /// (El patrón excluye <c>t.sinEvaluar</c> y <c>t.noCumple</c>, que empiezan igual.)
    /// </summary>
    [Theory]
    [InlineData(@"\bt\.si(?![A-Za-z])", "tickets.si")]
    [InlineData(@"\bt\.no(?![A-Za-z])", "tickets.no")]
    [InlineData(@"\bf\.cargaAcum\b", "fact.cargaAcum")]
    public void La_capa_de_dibujo_ya_no_lee_los_nombres_que_el_modelo_dejo_de_publicar(
        string patron, string nombre)
    {
        var m = Regex.Matches(CapaDeDibujo(), patron);

        Assert.True(m.Count == 0,
            $"render() todavía lee \"{nombre}\", que el modelo nuevo no publica: saldría undefined en el informe.");
    }

    /// <summary>Los tres estados de D2 tienen que estar los tres. Que el nombre viejo no esté no
    /// prueba que el nuevo se use: podrían haberse borrado las líneas.</summary>
    [Theory]
    [InlineData("t.cumple")]
    [InlineData("t.noCumple")]
    [InlineData("t.sinEvaluar")]
    [InlineData("t.denominadorPct")]
    public void La_capa_de_dibujo_lee_los_tres_estados_de_sla_y_su_denominador(string nombre) =>
        Assert.Contains(nombre, CapaDeDibujo(), StringComparison.Ordinal);

    /// <summary>D4: la cifra que sobrevive es <c>cargaRet</c>, con su unidad declarada. Si se
    /// hubieran borrado las dos tarjetas, el informe perdería la carga retirada sin decirlo.</summary>
    [Fact]
    public void La_carga_retirada_se_publica_una_sola_vez_y_con_su_unidad()
    {
        var dibujo = CapaDeDibujo();

        Assert.Contains("f.cargaRet", dibujo, StringComparison.Ordinal);
        Assert.Contains("f.unidadCargaRet", dibujo, StringComparison.Ordinal);
    }

    /// <summary>
    /// D7: <c>savLineas</c> se lee por nombre y el veredicto por fila viene del modelo
    /// (<c>l.contada</c>), no de una comparación recalculada en el dibujo — que es lo que marcaba
    /// como descartadas tres reservas que el total sí sumaba completas.
    /// </summary>
    [Fact]
    public void La_tabla_de_ahorro_de_advisor_lee_por_nombre_y_usa_el_veredicto_del_modelo()
    {
        var dibujo = CapaDeDibujo();

        foreach (var campo in new[] { "l.rec", "l.sub", "l.monto", "l.contada" })
            Assert.Contains(campo, dibujo, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\bl\[[012]\]"), dibujo);
    }

    /// <summary>
    /// D3: la cifra anualizada sale de <c>ahorro.anualizada</c>, que es <c>null</c> cuando la caída
    /// no se sostuvo tres meses cerrados. Multiplicar la tasa por doce es exactamente lo que la
    /// calculadora dejó de hacer, así que el dibujo tampoco puede hacerlo por su cuenta.
    /// </summary>
    [Fact]
    public void La_cifra_anualizada_sale_del_modelo_y_no_de_multiplicar_por_doce()
    {
        var dibujo = CapaDeDibujo();

        Assert.DoesNotContain("ahorro.dif*12", dibujo, StringComparison.Ordinal);
        Assert.Contains("anualizado(f.ahorro)", dibujo, StringComparison.Ordinal);
        // El helper vive junto a los de dibujo, unas líneas antes de render().
        Assert.Contains("a.anualizada===null", InformeValorHtmlExporter.Plantilla, StringComparison.Ordinal);
    }

    /// <summary>
    /// F1: la red de seguridad. Un monto ausente se imprime como "No publicado" y nunca como cero,
    /// porque <c>fmt()</c> lo intercepta antes de formatear. Si alguien le quita esa guarda,
    /// cualquier bloque suprimido pasa a decirle al cliente que gastó cero.
    /// </summary>
    [Fact]
    public void El_formateador_de_montos_intercepta_el_valor_ausente()
    {
        var t = InformeValorHtmlExporter.Plantilla;

        Assert.Contains("var NOPUB='No publicado';", t, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"function fmt\(n,d\)\{\s*if\(n===null\|\|n===undefined\|\|n===''\) return NOPUB;"), t);
    }

    /// <summary>El arranque por modelo embebido sigue siendo el único camino del artefacto.</summary>
    [Fact]
    public void La_plantilla_conserva_el_arranque_por_modelo_embebido()
    {
        var t = InformeValorHtmlExporter.Plantilla;

        Assert.Contains("if(EMBEDDED){", t, StringComparison.Ordinal);
        Assert.Contains("D=EMBEDDED; render();", t, StringComparison.Ordinal);
    }

    /// <summary>Substring de <paramref name="texto"/> entre las dos marcas (la de "hasta" no se
    /// incluye). Helper local para acotar el barrido a la región que interesa.</summary>
    private static string Recorte(string texto, string desde, string hasta)
    {
        var i = texto.IndexOf(desde, StringComparison.Ordinal);
        Assert.True(i >= 0, $"no se encontró la marca de inicio \"{desde}\"");
        var j = texto.IndexOf(hasta, i, StringComparison.Ordinal);
        Assert.True(j > i, $"no se encontró la marca de fin \"{hasta}\"");
        return texto.Substring(i, j - i);
    }

    /// <summary>Las tres primitivas nuevas existen: la plantilla no tenía dona, sparkline ni línea,
    /// y las secciones de esta entrega las necesitan.</summary>
    [Theory]
    [InlineData("function dona(")]
    [InlineData("function spark(")]
    [InlineData("function linea(")]
    public void La_plantilla_declara_las_primitivas_nuevas(string firma)
    {
        Assert.Contains(firma, InformeValorHtmlExporter.Plantilla, StringComparison.Ordinal);
    }

    /// <summary>ES5 estricto: la plantilla corre en navegadores viejos y dentro de un IIFE sin
    /// transpilar. Un arrow function o un template literal la rompen en silencio.
    ///
    /// <para>La plantilla no tiene una marca literal "/* ---- 6." — la sección de gráficos
    /// (5) cierra donde empieza "6. RENDER" (el mismo encabezado que usa <see cref="CapaDeDibujo"/>
    /// más arriba en este archivo, sin el bloque de asteriscos), así que se usa esa cadena como fin
    /// del recorte.</para></summary>
    [Fact]
    public void Las_primitivas_nuevas_no_usan_sintaxis_moderna()
    {
        var kit = Recorte(InformeValorHtmlExporter.Plantilla, "function dona(", "6. RENDER");
        Assert.DoesNotContain("=>", kit, StringComparison.Ordinal);
        Assert.DoesNotContain("`", kit, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\b(const|let)\s", kit);
    }
}
