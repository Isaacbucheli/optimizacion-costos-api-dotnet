using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Tarea 7 del plan de la entrega 2b: sin decisión propia de la Parte 1, port fiel de
/// <c>calcMatriz</c> a partir de <see cref="MatrizFila"/> (que ya llega resuelta por el
/// Recolector de la entrega 2a: <c>Ambito</c> ya es la etiqueta de pilar, no una celda de Excel).
/// </summary>
public sealed class RoadmapCalculadorTests
{
    private static MatrizFila Fila(
        int canonicalId, string ambito, string hallazgo, int avancePct,
        DateOnly? fecha = null, int? impactNumber = 1, string? prioridad = "1",
        string? esfuerzoTexto = null, string? registro = null, int resourceCount = 1, bool excluida = false) =>
        new(
            CanonicalId: canonicalId, MatrixCode: null, PillarNumber: 1, Ambito: ambito, Hallazgo: hallazgo,
            Fecha: fecha, ImpactNumber: impactNumber, Prioridad: prioridad, EsfuerzoTexto: esfuerzoTexto,
            AvancePct: avancePct, Registro: registro, ResourceCount: resourceCount, Excluida: excluida);

    [Fact]
    public void Sin_filas_devuelve_null()
    {
        Assert.Null(RoadmapCalculador.Calcular([]));
    }

    [Fact]
    public void Mapea_los_campos_basicos_de_un_item()
    {
        var fecha = new DateOnly(2026, 1, 10);
        var filas = new[] { Fila(1, "Seguridad", "Revisar asignaciones Owner", 50, fecha, 1, "1", "2-3 dias", "en curso", 3) };

        var modelo = RoadmapCalculador.Calcular(filas);

        var item = Assert.Single(modelo!.Items);
        Assert.Equal("Seguridad", item.Ambito);
        Assert.Equal("Revisar asignaciones Owner", item.Hallazgo);
        Assert.Equal("2026-01-10", item.Fecha);
        Assert.Equal(1, item.Impacto);
        Assert.Equal("1", item.Prioridad);
        Assert.Equal(50, item.AvancePct);
        Assert.Equal("en curso", item.Registro);
        Assert.Equal(3, item.RecomendacionesAsociadas); // = ResourceCount
    }

    [Fact]
    public void Fecha_nula_viaja_como_null_no_como_texto_vacio()
    {
        var filas = new[] { Fila(1, "Seguridad", "Hallazgo sin fecha", 0, fecha: null) };
        var item = Assert.Single(RoadmapCalculador.Calcular(filas)!.Items);
        Assert.Null(item.Fecha);
    }

    [Fact]
    public void Impacto_nulo_predetermina_a_cero()
    {
        var filas = new[] { Fila(1, "Seguridad", "Hallazgo sin impacto", 0, impactNumber: null) };
        var item = Assert.Single(RoadmapCalculador.Calcular(filas)!.Items);
        Assert.Equal(0, item.Impacto);
    }

    /// <summary>Port fiel de calcMatriz: quita el prefijo numérico de numeración manual ("1.2 ",
    /// coherente con la convención pilar.secuencia de la matriz) y trunca a 220 caracteres.</summary>
    [Fact]
    public void El_hallazgo_pierde_el_prefijo_numerico_y_se_trunca_a_220()
    {
        var textoLargo = "1.2 " + new string('x', 300);
        var filas = new[] { Fila(1, "Seguridad", textoLargo, 0) };

        var item = Assert.Single(RoadmapCalculador.Calcular(filas)!.Items);

        Assert.False(item.Hallazgo.StartsWith("1.2"));
        Assert.Equal(220, item.Hallazgo.Length);
    }

    [Fact]
    public void Ambito_en_blanco_usa_el_balde_sin_ambito()
    {
        var filas = new[] { Fila(1, "", "Hallazgo con ambito vacio", 0) };
        var item = Assert.Single(RoadmapCalculador.Calcular(filas)!.Items);
        Assert.Equal("(sin ámbito)", item.Ambito);
    }

    [Fact]
    public void Fila_con_hallazgo_y_ambito_vacios_se_descarta()
    {
        var filas = new[]
        {
            Fila(1, "", "", 0),
            Fila(2, "Seguridad", "Hallazgo real", 0),
        };

        var modelo = RoadmapCalculador.Calcular(filas);

        Assert.Equal(1, modelo!.Total);
        Assert.Equal("Hallazgo real", Assert.Single(modelo.Items).Hallazgo);
    }

    [Fact]
    public void Clasifica_cerrados_en_curso_y_sin_iniciar_por_avance()
    {
        var filas = new[]
        {
            Fila(1, "A", "Cerrado", 100),
            Fila(2, "A", "Cerrado tambien", 100),
            Fila(3, "A", "En curso", 40),
            Fila(4, "A", "Sin iniciar", 0),
            Fila(5, "A", "Sin iniciar negativo", -5),
        };

        var modelo = RoadmapCalculador.Calcular(filas);

        Assert.Equal(2, modelo!.Cerrados);
        Assert.Equal(1, modelo.EnCurso);
        Assert.Equal(2, modelo.SinIniciar);
    }

    [Fact]
    public void El_avance_promedio_se_redondea_a_un_decimal_como_js()
    {
        // promedio = (100+0+0)/3 = 33.333...; Math.round(33.333*10)/10 = 33.3
        var filas = new[]
        {
            Fila(1, "A", "Uno", 100),
            Fila(2, "A", "Dos", 0),
            Fila(3, "A", "Tres", 0),
        };

        var modelo = RoadmapCalculador.Calcular(filas);

        Assert.Equal(33.3, modelo!.AvancePromedio);
    }

    [Fact]
    public void Agrupa_por_ambito_y_ordena_por_cantidad_descendente()
    {
        var filas = new[]
        {
            Fila(1, "Seguridad", "Uno", 0, resourceCount: 2),
            Fila(2, "Seguridad", "Dos", 100, resourceCount: 3),
            Fila(3, "Costo", "Tres", 50, resourceCount: 1),
        };

        var modelo = RoadmapCalculador.Calcular(filas);

        Assert.Equal(2, modelo!.Ambitos.Count);
        Assert.Equal("Seguridad", modelo.Ambitos[0].Nombre); // 2 items > 1 item de Costo
        Assert.Equal(2, modelo.Ambitos[0].Cantidad);
        Assert.Equal(5, modelo.Ambitos[0].Recomendaciones); // suma de ResourceCount: 2+3
        Assert.Equal(50, modelo.Ambitos[0].AvancePromedio); // (0+100)/2 = 50
        Assert.Equal("Costo", modelo.Ambitos[1].Nombre);
    }

    /// <summary>Ver calcMatriz: <c>rec:sum(g.map(n))||g.length</c>. Si ResourceCount fuera 0 en
    /// todas las filas del ámbito, Recomendaciones cae al conteo de items, no queda en cero.</summary>
    [Fact]
    public void Recomendaciones_por_ambito_cae_al_conteo_de_items_si_la_suma_es_cero()
    {
        var filas = new[]
        {
            Fila(1, "Seguridad", "Uno", 0, resourceCount: 0),
            Fila(2, "Seguridad", "Dos", 0, resourceCount: 0),
        };

        var modelo = RoadmapCalculador.Calcular(filas);

        Assert.Equal(2, modelo!.Ambitos[0].Recomendaciones);
    }

    [Fact]
    public void Horas_pendientes_suma_el_esfuerzo_de_los_no_iniciados()
    {
        // Esfuerzo queda en 0 (brecha de datos: EsfuerzoTexto es texto libre sin parser numerico,
        // ver la nota de divergencia); la formula de la suma es la correcta y queda lista para
        // cuando exista una columna numerica real.
        var filas = new[]
        {
            Fila(1, "A", "Sin iniciar", 0, esfuerzoTexto: "2-3 dias"),
            Fila(2, "A", "En curso", 50, esfuerzoTexto: "1 dia"),
        };

        var modelo = RoadmapCalculador.Calcular(filas);

        Assert.Equal(0m, modelo!.HorasPendientes);
    }

    /// <summary>Excluida (is_excluded de la canónica) no filtra ninguna fila: la pantalla de la
    /// matriz tampoco lo hace, ver el docstring de <see cref="MatrizFila.Excluida"/>.</summary>
    [Fact]
    public void Excluida_no_filtra_la_fila()
    {
        var filas = new[] { Fila(1, "A", "Hallazgo excluido de la canonica", 0, excluida: true) };

        var modelo = RoadmapCalculador.Calcular(filas);

        Assert.Equal(1, modelo!.Total);
    }

    [Fact]
    public void Prioridad_viaja_cruda_sin_traducir_a_etiqueta()
    {
        var filas = new[] { Fila(1, "A", "Hallazgo", 0, prioridad: "2") };
        var item = Assert.Single(RoadmapCalculador.Calcular(filas)!.Items);
        Assert.Equal("2", item.Prioridad); // no "2 - MEDIA": esa traduccion no es parte de este contrato
    }
}
