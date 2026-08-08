using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Bloque de operación (Tarea 4 del plan de la entrega 2b): D0, D1, D2 y D10, más las "cinco
/// cuentas que definen la calidad" que le tocan a este bloque (duración en días contra horas).
/// Un caso de prueba por regla de cada decisión, más los casos borde que la decisión nombra.
/// </summary>
public sealed class OperacionCalculadorTests
{
    private static int _n;

    private static CasoRow Caso(
        string caso = "RF-1", string fecha = "2026-01-15", string? estado = "Cerrado",
        decimal? sla = 4m, decimal? duracion = 2m, string? cumple = "SI",
        string? categoria = "Cómputo", string? subcategoria = "Incidente",
        string? horario = "Horario Hábil") =>
        new($"h{++_n}", caso, DateOnly.Parse(fecha), estado, sla, duracion, cumple, categoria, subcategoria, horario);

    private static CasoRow CasoSinFecha(string caso = "RF-1") =>
        new($"h{++_n}", caso, null, "Cerrado", 4m, 2m, "SI", "Cómputo", "Incidente", "Hábil");

    private static ContextoInformeValor Contexto(string desde = "2026-01-01", string hasta = "2026-01-31") =>
        new(DateOnly.Parse(desde), DateOnly.Parse(hasta), DateOnly.Parse(hasta), null);

    // ---------- D0: el período filtra ----------

    [Fact]
    public void D0_Casos_fuera_del_rango_no_cuentan()
    {
        CasoRow[] casos =
        [
            Caso(caso: "RF-1", fecha: "2026-01-15"), // dentro
            Caso(caso: "RF-2", fecha: "2026-02-01"), // fuera, después
            Caso(caso: "RF-3", fecha: "2025-12-31"), // fuera, antes
        ];

        var m = OperacionCalculador.Calcular(casos, Contexto("2026-01-01", "2026-01-31"));

        Assert.Equal(1, m!.Total);
    }

    [Fact]
    public void D0_El_rango_es_cerrado_incluye_los_dos_extremos()
    {
        CasoRow[] casos = [Caso(caso: "RF-1", fecha: "2026-01-01"), Caso(caso: "RF-2", fecha: "2026-01-31")];

        var m = OperacionCalculador.Calcular(casos, Contexto("2026-01-01", "2026-01-31"));

        Assert.Equal(2, m!.Total);
    }

    [Fact]
    public void D0_Un_caso_sin_fecha_no_se_puede_confirmar_en_el_rango_y_se_excluye()
    {
        CasoRow[] casos = [Caso(caso: "RF-1", fecha: "2026-01-15"), CasoSinFecha(caso: "RF-2")];

        var m = OperacionCalculador.Calcular(casos, Contexto());

        Assert.Equal(1, m!.Total);
    }

    [Fact]
    public void D0_Sin_ningun_caso_dentro_del_rango_el_bloque_es_null()
    {
        CasoRow[] casos = [Caso(fecha: "2025-06-01")];

        var m = OperacionCalculador.Calcular(casos, Contexto("2026-01-01", "2026-01-31"));

        Assert.Null(m);
    }

    [Fact]
    public void D0_Sin_ningun_caso_el_bloque_es_null()
    {
        var m = OperacionCalculador.Calcular([], Contexto());

        Assert.Null(m);
    }

    // ---------- D1: un denominador por sección, balde residual explícito ----------

    [Fact]
    public void D1_Categorias_agrega_residual_sin_categoria_y_la_suma_da_el_total()
    {
        CasoRow[] casos =
        [
            Caso(caso: "1", categoria: "Cómputo"),
            Caso(caso: "2", categoria: "Cómputo"),
            Caso(caso: "3", categoria: ""),
        ];

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        Assert.Equal(3, m.Total);
        Assert.Equal(3, m.Categorias.Sum(c => c.Cantidad));
        Assert.Contains(m.Categorias, c => c.Nombre == "(sin categoría)" && c.Cantidad == 1);
    }

    [Fact]
    public void D1_Categorias_calcula_fuera_de_sla_y_mediana_por_su_propio_grupo()
    {
        CasoRow[] casos =
        [
            Caso(caso: "1", categoria: "Cómputo", cumple: "NO", duracion: 10m),
            Caso(caso: "2", categoria: "Cómputo", cumple: "SI", duracion: 2m),
            Caso(caso: "3", categoria: "Redes", cumple: "SI", duracion: 100m),
        ];

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        var computo = m.Categorias.Single(c => c.Nombre == "Cómputo");
        Assert.Equal(2, computo.Cantidad);
        Assert.Equal(1, computo.FueraDeSla);
        Assert.Equal(6.0, computo.MedianaHoras, 5); // mediana de [10,2] = 6
    }

    [Fact]
    public void D1_Frentes_agrega_residual_sin_subcategoria_y_la_suma_da_el_total()
    {
        CasoRow[] casos = [Caso(caso: "1", subcategoria: "Incidente"), Caso(caso: "2", subcategoria: null)];

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        Assert.Equal(2, m.Frentes.Sum(f => f.Cantidad));
        Assert.Contains(m.Frentes, f => f.Nombre == "(sin subcategoría)" && f.Cantidad == 1 && !f.EsReactivo);
    }

    [Fact]
    public void D1_Horario_no_lleva_residual_su_denominador_propio_puede_ser_menor_al_total()
    {
        CasoRow[] casos = [Caso(caso: "1", horario: "Horario Hábil"), Caso(caso: "2", horario: "")];

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        Assert.Equal(2, m.Total);
        Assert.Equal(1, m.PorHorario.Sum(h => (int)h[1]!)); // el propio denominador de "hor" es 1, no 2
    }

    [Fact]
    public void D1_Horario_le_quita_el_prefijo_Horario_antes_de_agrupar()
    {
        CasoRow[] casos = [Caso(horario: "Horario Hábil")];

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        Assert.Equal("Hábil", m.PorHorario[0][0]);
    }

    // ---------- D2: "dentro de SLA" tiene una sola definición ----------

    [Theory]
    [InlineData("SI")]
    [InlineData("SÍ")]
    [InlineData("YES")]
    [InlineData("si")]
    [InlineData(" Si ")]
    public void D2_Cumple_reconoce_las_variantes_afirmativas(string cumpleTexto)
    {
        var m = OperacionCalculador.Calcular([Caso(cumple: cumpleTexto)], Contexto())!;

        Assert.Equal(1, m.Cumple);
        Assert.Equal(0, m.NoCumple);
        Assert.Equal(0, m.SinEvaluar);
    }

    [Fact]
    public void D2_NoCumple_solo_reconoce_NO()
    {
        var m = OperacionCalculador.Calcular([Caso(cumple: "NO")], Contexto())!;

        Assert.Equal(1, m.NoCumple);
        Assert.Equal(0, m.Cumple);
        Assert.Equal(0, m.SinEvaluar);
    }

    [Theory]
    [InlineData("")]
    [InlineData((string?)null)]
    [InlineData("Pendiente")]
    [InlineData("N/A")]
    public void D2_Cualquier_otro_valor_es_sin_evaluar_no_cumple_por_omision(string? cumpleTexto)
    {
        var m = OperacionCalculador.Calcular([Caso(cumple: cumpleTexto)], Contexto())!;

        Assert.Equal(1, m.SinEvaluar);
        Assert.Equal(0, m.Cumple);
        Assert.Equal(0, m.NoCumple);
    }

    [Fact]
    public void D2_El_KPI_usa_cumple_mas_no_cumple_como_denominador_y_lo_declara()
    {
        CasoRow[] casos =
        [
            Caso(caso: "1", cumple: "SI"), Caso(caso: "2", cumple: "SI"), Caso(caso: "3", cumple: "SI"),
            Caso(caso: "4", cumple: "NO"),
            Caso(caso: "5", cumple: ""), Caso(caso: "6", cumple: ""), Caso(caso: "7", cumple: ""),
            Caso(caso: "8", cumple: ""), Caso(caso: "9", cumple: ""), Caso(caso: "10", cumple: ""),
        ];

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        Assert.Equal(10, m.Total);
        Assert.Equal(3, m.Cumple);
        Assert.Equal(1, m.NoCumple);
        Assert.Equal(6, m.SinEvaluar);
        Assert.Equal(4, m.DenominadorPctCumplimiento); // 3+1, NUNCA 10
        Assert.Equal(75.0, m.PctCumplimiento, 5);
    }

    [Fact]
    public void D2_MediaHorasDentroSla_promedia_solo_los_que_cumplen()
    {
        CasoRow[] casos =
        [
            Caso(caso: "1", cumple: "SI", duracion: 2m),
            Caso(caso: "2", cumple: "SI", duracion: 4m),
            Caso(caso: "3", cumple: "", duracion: 100m), // sin evaluar: no puede colarse en el promedio
        ];

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        Assert.Equal(3.0, m.MediaHorasDentroSla, 5); // (2+4)/2, nunca (2+4+100)/3
    }

    [Fact]
    public void D2_Detalle_publica_los_tres_estados_no_fuerza_un_binario()
    {
        var m = OperacionCalculador.Calcular([Caso(caso: "RF-1", cumple: "")], Contexto())!;

        Assert.Equal("SIN EVALUAR", m.Detalle[0][6]);
    }

    [Fact]
    public void D2_FueraDeSla_y_serie_mensual_cuentan_solo_no_cumple_nunca_sin_evaluar()
    {
        CasoRow[] casos = [Caso(caso: "1", cumple: "NO"), Caso(caso: "2", cumple: "")];

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        Assert.Single(m.FueraDeSla);
        Assert.Equal(1, (int)m.SerieMensual[0][2]!);
    }

    [Fact]
    public void D2_La_tabla_de_detalle_usa_la_misma_pertenencia_que_el_KPI()
    {
        CasoRow[] casos = [Caso(caso: "1", cumple: "SI"), Caso(caso: "2", cumple: "NO"), Caso(caso: "3", cumple: "")];

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        var porCaso = m.Detalle.ToDictionary(f => (string)f[0]!, f => (string)f[6]!);
        Assert.Equal("SI", porCaso["1"]);
        Assert.Equal("NO", porCaso["2"]);
        Assert.Equal("SIN EVALUAR", porCaso["3"]);
    }

    // ---------- D10: la proactividad se titula por volumen ----------

    [Fact]
    public void D10_Casos_sin_subcategoria_no_son_reactivos_ni_cuentan_como_proactivos_por_omision()
    {
        CasoRow[] casos =
        [
            Caso(caso: "1", subcategoria: "Falla de servicio"), // reactivo
            Caso(caso: "2", subcategoria: null),
            Caso(caso: "3", subcategoria: null),
        ];

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        Assert.Equal(1, m.CasosReactivos);
        Assert.Equal(2, m.CasosSinSubcategoria);
        // proactivo real = Total - CasosReactivos - CasosSinSubcategoria = 3-1-2 = 0, nunca 2.
    }

    [Fact]
    public void D10_La_metrica_de_frentes_y_la_de_volumen_pueden_divergir()
    {
        var casos = new List<CasoRow>
        {
            Caso(caso: "R1", subcategoria: "Falla masiva"),
            Caso(caso: "R2", subcategoria: "Falla masiva"),
        };
        for (var i = 0; i < 400; i++) casos.Add(Caso(caso: $"P{i}", subcategoria: "Solicitud de acceso"));

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        // Por frentes: 1 de 2 frentes es reactivo -> 50%.
        Assert.Equal(2, m.TotalFrentes);
        Assert.Equal(1, m.FrentesReactivos);
        // Por volumen: 2 de 402 casos son reactivos -> ~0.5%. El titular usa ESTA.
        Assert.Equal(402, m.Total);
        Assert.Equal(2, m.CasosReactivos);
    }

    [Fact]
    public void D10_Un_frente_es_reactivo_por_palabras_de_falla_no_por_su_volumen()
    {
        // "caído" (termina en o) no matchea el regex de la plantilla, que solo lista "caida"/
        // "caída" (terminan en a): mismo límite que la plantilla, no un defecto del puerto.
        var m = OperacionCalculador.Calcular([Caso(subcategoria: "Error de autenticación")], Contexto())!;

        Assert.True(m.Frentes[0].EsReactivo);
    }

    // ---------- "Cinco cuentas" del spec que le tocan a este bloque: duración en días u horas ----------

    [Fact]
    public void Duracion_se_multiplica_por_24_cuando_el_p90_sugiere_dias()
    {
        // Nearest-rank sobre 10 valores en p=0.9 -> índice floor(10*0.9)=9 -> el mayor. 9x1 + 1x20: p90=20<30.
        var casos = Enumerable.Range(0, 9).Select(i => Caso(caso: $"A{i}", duracion: 1m))
            .Append(Caso(caso: "A9", duracion: 20m)).ToList();

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        Assert.True(m.DuracionOriginalEnDias);
        Assert.Equal(24.0, m.MedianaHoras, 5); // mediana cruda = 1 día -> 24 horas
    }

    [Fact]
    public void Duracion_no_se_multiplica_cuando_el_p90_ya_es_grande()
    {
        var casos = Enumerable.Range(0, 10).Select(i => Caso(caso: $"A{i}", duracion: 40m)).ToList();

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        Assert.False(m.DuracionOriginalEnDias);
        Assert.Equal(40.0, m.MedianaHoras, 5);
    }

    [Fact]
    public void Duracion_toda_en_cero_no_dispara_la_heuristica_de_dias()
    {
        var casos = Enumerable.Range(0, 10).Select(i => Caso(caso: $"A{i}", duracion: 0m)).ToList();

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        Assert.False(m.DuracionOriginalEnDias); // p90=0; la guarda exige p90>0
    }

    // ---------- Comportamiento general (paridad, no decisión) ----------

    [Fact]
    public void Cerrados_reconoce_cerrado_closed_y_resuelto_sin_importar_mayusculas()
    {
        CasoRow[] casos =
        [
            Caso(caso: "1", estado: "Cerrado"), Caso(caso: "2", estado: "CLOSED"),
            Caso(caso: "3", estado: "Resuelto"), Caso(caso: "4", estado: "Abierto"),
        ];

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        Assert.Equal(3, m.Cerrados);
    }

    [Fact]
    public void Detalle_se_ordena_por_fecha_descendente()
    {
        CasoRow[] casos = [Caso(caso: "viejo", fecha: "2026-01-05"), Caso(caso: "nuevo", fecha: "2026-01-20")];

        var m = OperacionCalculador.Calcular(casos, Contexto())!;

        Assert.Equal("nuevo", m.Detalle[0][0]);
        Assert.Equal("viejo", m.Detalle[1][0]);
    }

    [Fact]
    public void Racha_cuenta_los_meses_finales_sin_incumplir_y_sus_casos()
    {
        CasoRow[] casos =
        [
            Caso(caso: "1", fecha: "2025-11-15", cumple: "NO"), // rompe la racha
            Caso(caso: "2", fecha: "2025-12-10", cumple: "SI"),
            Caso(caso: "3", fecha: "2026-01-10", cumple: "SI"),
            Caso(caso: "4", fecha: "2026-01-20", cumple: "SI"),
        ];

        var m = OperacionCalculador.Calcular(casos, Contexto("2025-11-01", "2026-01-31"))!;

        Assert.Equal(2, m.RachaMesesSinIncumplir); // dic y ene, sin contar nov
        Assert.Equal(3, m.RachaCasos); // 1 (dic) + 2 (ene)
    }
}
