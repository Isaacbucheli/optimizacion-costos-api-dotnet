using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Tarea 3 del plan de la entrega 2b (D0, D3, D4, D5, D6, D14). Un caso por regla, más los bordes
/// que cada decisión menciona explícitamente. Los números de cada escenario de D3 están calculados
/// a mano en los comentarios de cada prueba: son los mismos que alimentan la lista de divergencias.
/// </summary>
public sealed class ConsumoCalculadorTests
{
    private static int _hash;

    private static FacturacionRow Fila(
        string recurso, decimal pvp, int anio, int mes,
        string? categoria = "Cómputo", string? subscriptionId = "sub-1", string? rg = "rg-1",
        string? subscriptionName = "Suscripción Demo", string? costCenter = null) =>
        new(
            Hash: $"h{++_hash}", Tenant: null, SubscriptionName: subscriptionName, SubscriptionId: subscriptionId,
            ResourceGroup: rg, ResourceName: recurso, CostCenter: costCenter, Category: categoria,
            Subcategory: null, Service: null, Quantity: null, Unit: null, Rate: null,
            Pvp: pvp, Year: (short)anio, Month: (byte)mes);

    private static ContextoInformeValor Contexto(
        int inicioAnio, int inicioMes, int finAnio, int finMes,
        IReadOnlyList<string>? forzados = null) =>
        new(
            new DateOnly(inicioAnio, inicioMes, 1),
            new DateOnly(finAnio, finMes, DateTime.DaysInMonth(finAnio, finMes)),
            Corte: new DateOnly(finAnio, finMes, DateTime.DaysInMonth(finAnio, finMes)),
            MesesParcialesForzados: forzados);

    // ---------------------------------------------------------------- D0 ----

    [Fact]
    public void D0_filtra_filas_fuera_del_rango_del_contexto()
    {
        var filas = new[]
        {
            Fila("vm-1", 100m, 2026, 1),
            Fila("vm-1", 999m, 2026, 3), // fuera de rango: el contexto termina en febrero
        };
        var contexto = Contexto(2026, 1, 2026, 2);

        var modelo = ConsumoCalculador.Calcular(filas, filasAntesDeFusionar: 2, contexto: contexto);

        Assert.NotNull(modelo);
        Assert.Equal(100m, modelo!.Total);
        Assert.Equal("2026-01", Assert.Single(modelo.SerieMensual)[0]);
        // Punto C: FilasEnRango cuenta solo lo que paso el filtro D0 (1 fila), distinto del
        // parametro filasAntesDeFusionar (2, la carga completa) que sigue publicandose en Filas.
        Assert.Equal(1, modelo.FilasEnRango);
        Assert.Equal(2, modelo.Filas);
    }

    [Fact]
    public void D0_incluye_filas_en_los_bordes_del_rango()
    {
        var filas = new[]
        {
            Fila("vm-1", 10m, 2026, 1), // primer mes del rango
            Fila("vm-1", 10m, 2026, 2),
            Fila("vm-1", 10m, 2026, 3), // ultimo mes del rango
            Fila("vm-1", 999m, 2026, 4), // fuera
        };
        var contexto = Contexto(2026, 1, 2026, 3);

        var modelo = ConsumoCalculador.Calcular(filas, filasAntesDeFusionar: 4, contexto: contexto);

        Assert.Equal(30m, modelo!.Total);
        Assert.Equal(3, modelo.SerieMensual.Count);
        Assert.Equal(3, modelo.FilasEnRango);
    }

    [Fact]
    public void D0_sin_ninguna_fila_en_rango_devuelve_null()
    {
        var filas = new[] { Fila("vm-1", 100m, 2025, 12) };
        var contexto = Contexto(2026, 1, 2026, 1);

        Assert.Null(ConsumoCalculador.Calcular(filas, filasAntesDeFusionar: 1, contexto: contexto));
    }

    /// <summary>
    /// Consecuencia directa de D0, documentada como divergencia: la plantilla (que consume el
    /// archivo entero) siempre encuentra el mes base interanual si existe en el Excel. La
    /// calculadora, filtrada al rango, no lo encuentra si el rango no lo incluye, aunque el dato
    /// exista en la base.
    /// </summary>
    [Fact]
    public void D0_la_comparativa_interanual_exige_que_el_mes_base_tambien_este_en_rango()
    {
        var filas = new[]
        {
            Fila("vm-1", 1000m, 2025, 1, categoria: "Storage"),
            Fila("vm-1", 1200m, 2026, 1, categoria: "Storage"),
        };

        var conAmbosMeses = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 2, contexto: Contexto(2025, 1, 2026, 1));
        Assert.NotNull(conAmbosMeses!.Comparativa);
        Assert.Equal("2025-01", conAmbosMeses.Comparativa!.MesBase);
        Assert.Equal("2026-01", conAmbosMeses.Comparativa.MesComparado);

        var soloUltimoMes = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 2, contexto: Contexto(2026, 1, 2026, 1));
        Assert.Null(soloUltimoMes!.Comparativa);
    }

    // ---------------------------------------------------------------- D14 ---

    [Fact]
    public void D14_filas_es_el_parametro_de_filas_antes_de_fusionar_no_el_conteo_de_entrada()
    {
        var filas = new[] { Fila("vm-1", 100m, 2026, 1), Fila("vm-2", 50m, 2026, 1) };

        var modelo = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 9137, contexto: Contexto(2026, 1, 2026, 1));

        Assert.Equal(9137, modelo!.Filas);
        // Punto C: FilasEnRango es independiente del parametro, sale de contar la lista de entrada
        // ya filtrada por D0 (las 2 filas de este caso, ambas en rango).
        Assert.Equal(2, modelo.FilasEnRango);
    }

    // ---------------------------------------------------------------- D4 ----

    /// <summary>
    /// vm-b factura enero y febrero y se detiene; vm-a factura los tres meses (sigue activo en
    /// ultCompleto = marzo). CargaRetirada tiene que ser el importe del ÚLTIMO mes de vm-b (70,
    /// febrero), no la suma de sus dos meses (120) ni nada de vm-a.
    /// </summary>
    [Fact]
    public void D4_carga_retirada_suma_una_vez_por_recurso_el_ultimo_mes_de_los_que_se_dieron_de_baja()
    {
        var filas = new[]
        {
            Fila("vm-a", 100m, 2026, 1), Fila("vm-a", 100m, 2026, 2), Fila("vm-a", 100m, 2026, 3),
            Fila("vm-b", 50m, 2026, 1), Fila("vm-b", 70m, 2026, 2),
        };

        var modelo = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 5, contexto: Contexto(2026, 1, 2026, 3));

        Assert.Equal(1, modelo!.BajasDefinitivas);
        Assert.Equal(70m, modelo.CargaRetirada);
        Assert.False(string.IsNullOrWhiteSpace(modelo.UnidadCargaRetirada));
    }

    // ------------------------------------------------------- D5, D6, D4 -----
    // Mismo escenario para las tres: cuatro meses, un recurso estable (vm-const), uno que se da de
    // baja de verdad en un mes NO parcial (vm-drops-mar) y uno que "se da de baja" solo porque el
    // último mes es parcial y todavía no facturó (vm-drops-abr-artefacto).

    private static IReadOnlyList<FacturacionRow> EscenarioMesParcialAlFinal() =>
    [
        Fila("vm-const", 100m, 2026, 1), Fila("vm-drops-mar", 50m, 2026, 1), Fila("vm-drops-abr-artefacto", 30m, 2026, 1),
        Fila("vm-const", 100m, 2026, 2), Fila("vm-drops-mar", 70m, 2026, 2), Fila("vm-drops-abr-artefacto", 30m, 2026, 2),
        Fila("vm-const", 100m, 2026, 3), /* vm-drops-mar ya no factura */ Fila("vm-drops-abr-artefacto", 30m, 2026, 3),
        Fila("vm-const", 100m, 2026, 4), /* vm-drops-abr-artefacto no facturo (todavia): mes parcial */
    ];

    private static IReadOnlyList<object?> SerieDelMes(ConsumoModelo modelo, string mes) =>
        modelo.Serie.Single(fila => (string)fila[0]! == mes);

    [Fact]
    public void D5_las_bajas_del_mes_parcial_se_excluyen_del_conteo()
    {
        var contexto = Contexto(2026, 1, 2026, 4, forzados: ["2026-04"]);
        var modelo = ConsumoCalculador.Calcular(
            EscenarioMesParcialAlFinal(), filasAntesDeFusionar: 9, contexto: contexto);

        // Sin el fix, abril mostraria baja=1 (vm-drops-abr-artefacto "desaparece"): es exactamente
        // el defecto que D5 corrige.
        Assert.Equal(0, (int)SerieDelMes(modelo!, "2026-04")[3]!);
    }

    [Fact]
    public void D5_las_bajas_de_un_mes_no_parcial_se_cuentan_normal_control()
    {
        var contexto = Contexto(2026, 1, 2026, 4, forzados: ["2026-04"]);
        var modelo = ConsumoCalculador.Calcular(
            EscenarioMesParcialAlFinal(), filasAntesDeFusionar: 9, contexto: contexto);

        // Marzo NO es parcial: vm-drops-mar realmente dejo de facturar ahi, y tiene que contar.
        Assert.Equal(1, (int)SerieDelMes(modelo!, "2026-03")[3]!);
    }

    [Fact]
    public void D5_el_monto_retirado_del_mes_parcial_tambien_se_excluye()
    {
        var contexto = Contexto(2026, 1, 2026, 4, forzados: ["2026-04"]);
        var modelo = ConsumoCalculador.Calcular(
            EscenarioMesParcialAlFinal(), filasAntesDeFusionar: 9, contexto: contexto);

        Assert.Equal(0m, (decimal)SerieDelMes(modelo!, "2026-04")[5]!); // indice 5 = monto retirado del mes
    }

    /// <summary>
    /// D6: vm-drops-abr-artefacto sigue activo hasta el ultimo mes cerrado (marzo = ultCompleto):
    /// no es una baja definitiva, aunque abril (parcial) lo muestre ausente.
    /// </summary>
    [Fact]
    public void D4_D6_un_recurso_que_solo_falta_en_el_mes_parcial_no_es_baja_definitiva()
    {
        var contexto = Contexto(2026, 1, 2026, 4, forzados: ["2026-04"]);
        var modelo = ConsumoCalculador.Calcular(
            EscenarioMesParcialAlFinal(), filasAntesDeFusionar: 9, contexto: contexto);

        Assert.Equal("2026-03", modelo!.UltimoMesCompleto);
        Assert.Equal(1, modelo.BajasDefinitivas); // solo vm-drops-mar
        Assert.Equal(70m, modelo.CargaRetirada); // el ultimo mes de vm-drops-mar (febrero)
    }

    /// <summary>
    /// D6: un recurso intermitente (falta un mes y vuelve) suma una "desconexión del mes" en la
    /// serie pero cero "bajas definitivas", porque al final del período sigue activo.
    /// </summary>
    [Fact]
    public void D6_recurso_intermitente_cuenta_desconexion_del_mes_pero_no_baja_definitiva()
    {
        var filas = new[]
        {
            Fila("vm-boring", 100m, 2026, 1), Fila("vm-intermitente", 100m, 2026, 1),
            Fila("vm-boring", 100m, 2026, 2), // vm-intermitente no factura en febrero
            Fila("vm-boring", 100m, 2026, 3), Fila("vm-intermitente", 100m, 2026, 3),
            Fila("vm-boring", 100m, 2026, 4), Fila("vm-intermitente", 100m, 2026, 4),
        };
        var contexto = Contexto(2026, 1, 2026, 4, forzados: []); // ninguno parcial, ver el analisis en el reporte

        var modelo = ConsumoCalculador.Calcular(filas, filasAntesDeFusionar: 8, contexto: contexto);

        Assert.Equal(1, (int)SerieDelMes(modelo!, "2026-02")[3]!); // la desconexion de febrero
        Assert.Equal(0, modelo!.BajasDefinitivas); // vuelve a facturar: no es una baja definitiva
    }

    // ---------------------------------------------------------------- D3 ----
    // Los tres bugs historicos y las cinco reglas nuevas. Los numeros de cada caso estan resueltos
    // a mano en el comentario: son la base de los ejemplos numericos del reporte de divergencias.

    /// <summary>
    /// Bug historico #1: una categoria de solo notas de credito. Formula vieja (pico en cero):
    /// pico=0, pi=0, post=v[1..5]=[-40,-60,-45,-55,-48], fin=-49.6, dif=0-(-49.6)=49.6&gt;0 y
    /// fin(-49.6)&lt;pico*0.6(0) -&gt; la plantilla publica un ahorro de 49.6/mes sobre una
    /// categoria que nunca facturo en positivo. La regla nueva exige LineaBase&gt;0 antes de
    /// evaluar nada: mediana de los primeros tres valores = mediana(-50,-40,-60) = -50 &lt;= 0,
    /// la categoria queda descartada.
    /// </summary>
    [Fact]
    public void D3_categoria_de_solo_valores_negativos_no_genera_ahorro()
    {
        var filas = MesesDeCategoria("Ajustes", -50m, -40m, -60m, -45m, -55m, -48m);
        // forzados=[] para aislar D3 de la heuristica automatica de meses parciales (D0/deteccion),
        // que con solo una categoria puede marcar un mes por su cuenta: ver el reporte de la tarea.
        var modelo = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 6, contexto: Contexto(2026, 1, 2026, 6, forzados: []));

        Assert.Null(modelo!.Ahorro);
    }

    /// <summary>
    /// Bug historico #2: un solo mes de volatilidad no es una linea base. Formula vieja: pico=900
    /// (el mes 2, un pico aislado), post=v[2..5]=[500,490,510,495], fin=498.75,
    /// dif=900-498.75=401.25&gt;0 y fin&lt;900*0.6(540) -&gt; ahorro falso de ~401/mes sobre una
    /// categoria que en realidad esta estable en ~500. La mediana (500) ve a traves del pico y el
    /// promedio de los ultimos tres meses (498.33) no baja del 60% de esa mediana (300): no hay
    /// caida sostenida real, la categoria no califica.
    /// </summary>
    [Fact]
    public void D3_un_pico_de_un_solo_mes_no_dispara_ahorro_falso()
    {
        var filas = MesesDeCategoria("Redes", 500m, 900m, 500m, 490m, 510m, 495m);
        var modelo = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 6, contexto: Contexto(2026, 1, 2026, 6, forzados: []));

        Assert.Null(modelo!.Ahorro);
    }

    /// <summary>
    /// Caida limpia y sostenida en una sola categoria (sin otras que compitan por el neteo).
    /// LineaBase = mediana(1000,1050,980,1020,1010,990) = 1005. Fin = promedio(380,390,370) = 380.
    /// Los tres ultimos meses estan bajo el umbral (1005*0.6=603) uno por uno: sostenido = 3.
    /// TasaMensual = 1005-380 = 625; por sostenido>=3, Anualizada = 625*12 = 7500.
    /// </summary>
    [Fact]
    public void D3_caida_sostenida_tres_meses_cerrados_se_anualiza()
    {
        var filas = MesesDeCategoria("Backup", 1000m, 1050m, 980m, 1020m, 1010m, 990m, 380m, 390m, 370m);
        var modelo = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 9, contexto: Contexto(2026, 1, 2026, 9, forzados: []));

        var ahorro = modelo!.Ahorro;
        Assert.NotNull(ahorro);
        Assert.Equal("Backup", ahorro!.Categoria);
        Assert.Equal(1005m, ahorro.LineaBase);
        Assert.Equal(380m, ahorro.Fin);
        Assert.Equal(625m, ahorro.TasaMensual);
        Assert.Equal(3, ahorro.MesesSostenido);
        Assert.Equal(7500m, ahorro.Anualizada);
    }

    /// <summary>
    /// Mismos seis meses base, pero el septimo mes (el mas antiguo de los "tres finales") vuelve a
    /// subir por encima del umbral: 700, 380, 370. La caida promedio sigue calificando
    /// (fin=483.33&lt;603), asi que se publica una tasa mensual, pero el conteo hacia atras se
    /// corta en el mes que no cumple: sostenido = 2 (370 y 380 califican, 700 no) -&gt; sin
    /// anualizar.
    /// </summary>
    [Fact]
    public void D3_caida_sostenida_menor_a_tres_meses_no_se_anualiza()
    {
        var filas = MesesDeCategoria("Backup", 1000m, 1050m, 980m, 1020m, 1010m, 990m, 700m, 380m, 370m);
        var modelo = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 9, contexto: Contexto(2026, 1, 2026, 9, forzados: []));

        var ahorro = modelo!.Ahorro;
        Assert.NotNull(ahorro);
        Assert.Equal(2, ahorro!.MesesSostenido);
        Assert.Null(ahorro.Anualizada);
        Assert.True(ahorro.TasaMensual > 0); // la tasa mensual SI se publica aunque no se anualice
    }

    [Fact]
    public void D3_menos_de_seis_meses_no_parciales_no_es_elegible_para_ahorro()
    {
        var filas = MesesDeCategoria("Backup", 1000m, 1000m, 1000m, 300m, 300m);
        var modelo = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 5, contexto: Contexto(2026, 1, 2026, 5, forzados: []));

        Assert.Null(modelo!.Ahorro);
    }

    /// <summary>
    /// Backup cae sostenido (dif=625, ver la prueba de arriba) pero Compute crece en el mismo
    /// periodo: LineaBase=mediana(500,520,510,530,515,505)=512.5, Fin=promedio(900,920,910)=910,
    /// dif=512.5-910=-397.5. Neto=625-397.5=227.5: la tasa publicada es la neta (227.5), no el
    /// 625 aislado de Backup. Backup sigue siendo la categoria que se nombra (es la que de verdad
    /// bajo), pero el numero que se destaca ya no ignora que Compute subio.
    /// </summary>
    [Fact]
    public void D3_categorias_que_suben_netean_la_tasa_publicada()
    {
        var filas = MesesDeCategoria("Backup", 1000m, 1050m, 980m, 1020m, 1010m, 990m, 380m, 390m, 370m)
            .Concat(MesesDeCategoria("Compute", 500m, 520m, 510m, 530m, 515m, 505m, 900m, 920m, 910m))
            .ToList();
        var modelo = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 18, contexto: Contexto(2026, 1, 2026, 9, forzados: []));

        var ahorro = modelo!.Ahorro;
        Assert.NotNull(ahorro);
        Assert.Equal("Backup", ahorro!.Categoria);
        Assert.Equal(227.5m, ahorro.TasaMensual);
        Assert.Equal(2730m, ahorro.Anualizada); // 227.5 * 12, sostenido sigue siendo 3
    }

    /// <summary>
    /// Igual que la prueba anterior, pero Compute crece todavia mas (a 1200 sostenido): el
    /// crecimiento neto (512.5-1200=-687.5) supera a la caida de Backup (625), Neto=625-687.5=
    /// -62.5 &lt;= 0. No hay, en agregado, ningun ahorro real que publicar: se suprime el bloque
    /// entero en vez de destacar la caida de Backup ignorando que el conjunto crecio.
    /// </summary>
    [Fact]
    public void D3_si_el_crecimiento_neto_supera_la_caida_no_se_publica_ahorro()
    {
        var filas = MesesDeCategoria("Backup", 1000m, 1050m, 980m, 1020m, 1010m, 990m, 380m, 390m, 370m)
            .Concat(MesesDeCategoria("Compute", 500m, 520m, 510m, 530m, 515m, 505m, 1200m, 1200m, 1200m))
            .ToList();
        var modelo = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 18, contexto: Contexto(2026, 1, 2026, 9, forzados: []));

        Assert.Null(modelo!.Ahorro);
    }

    private static IReadOnlyList<FacturacionRow> MesesDeCategoria(string categoria, params decimal[] valoresPorMes)
    {
        var filas = new List<FacturacionRow>();
        for (var i = 0; i < valoresPorMes.Length; i++)
        {
            var mes = i + 1;
            filas.Add(Fila($"recurso-{categoria}", valoresPorMes[i], 2026, mes, categoria: categoria));
        }
        return filas;
    }

    // -------------------------------------------------- Tri-estado de forzados ----

    private static IReadOnlyList<FacturacionRow> EscenarioParaHeuristica() =>
    [
        Fila("vm-1", 1000m, 2026, 1), Fila("vm-1", 1000m, 2026, 2),
        Fila("vm-1", 1000m, 2026, 3), Fila("vm-1", 500m, 2026, 4),
    ];

    [Fact]
    public void Forzados_nulo_aplica_la_heuristica_automatica()
    {
        var modelo = ConsumoCalculador.Calcular(
            EscenarioParaHeuristica(), filasAntesDeFusionar: 4, contexto: Contexto(2026, 1, 2026, 4, forzados: null));

        Assert.Equal(["2026-04"], modelo!.MesesParciales);
        Assert.Equal(["2026-04"], modelo.MesesParcialesDetectadosAuto);
        Assert.Equal("2026-03", modelo.UltimoMesCompleto);
        Assert.Empty(modelo.MesesParcialesInexistentes); // sin declaracion del consultor, nada que avisar
    }

    [Fact]
    public void Forzados_lista_vacia_declara_ningun_mes_parcial_aunque_la_heuristica_marque_uno()
    {
        var modelo = ConsumoCalculador.Calcular(
            EscenarioParaHeuristica(), filasAntesDeFusionar: 4, contexto: Contexto(2026, 1, 2026, 4, forzados: []));

        Assert.Empty(modelo!.MesesParciales);
        Assert.Equal(["2026-04"], modelo.MesesParcialesDetectadosAuto); // el diagnostico se sigue calculando
        Assert.Equal("2026-04", modelo.UltimoMesCompleto);
        Assert.Empty(modelo.MesesParcialesInexistentes);
    }

    [Fact]
    public void Forzados_con_meses_manda_sobre_la_heuristica()
    {
        var modelo = ConsumoCalculador.Calcular(
            EscenarioParaHeuristica(), filasAntesDeFusionar: 4, contexto: Contexto(2026, 1, 2026, 4, forzados: ["2026-03"]));

        Assert.Equal(["2026-03"], modelo!.MesesParciales);
        Assert.Equal(["2026-04"], modelo.MesesParcialesDetectadosAuto);
        Assert.Equal("2026-04", modelo.UltimoMesCompleto); // abril no esta forzado: cuenta como cerrado
        Assert.Empty(modelo.MesesParcialesInexistentes); // "2026-03" existe: no hay nada que avisar
    }

    /// <summary>Punto B del feedback de la coordinacion (spec §12.3.3): un mes forzado que no
    /// existe no se aplica al calculo, pero SI se reporta, a diferencia del silencio de
    /// calcFact.</summary>
    [Fact]
    public void Forzados_con_un_mes_que_no_existe_no_se_aplica_y_queda_reportado()
    {
        var modelo = ConsumoCalculador.Calcular(
            EscenarioParaHeuristica(), filasAntesDeFusionar: 4, contexto: Contexto(2026, 1, 2026, 4, forzados: ["2099-12"]));

        Assert.Empty(modelo!.MesesParciales); // no se aplica: "2099-12" no existe en el insumo
        Assert.Equal(["2099-12"], modelo.MesesParcialesInexistentes); // pero queda avisado
    }

    /// <summary>Mezcla: un mes real (se aplica), uno inexistente repetido dos veces (se avisa una
    /// sola vez) y la heuristica queda de lado por completo, igual que con cualquier lista no
    /// vacia.</summary>
    [Fact]
    public void Forzados_mixtos_aplica_los_que_existen_y_deduplica_los_que_avisa()
    {
        var modelo = ConsumoCalculador.Calcular(
            EscenarioParaHeuristica(),
            filasAntesDeFusionar: 4,
            contexto: Contexto(2026, 1, 2026, 4, forzados: ["2026-02", "2099-12", "2099-12"]));

        Assert.Equal(["2026-02"], modelo!.MesesParciales);
        Assert.Equal(["2099-12"], modelo.MesesParcialesInexistentes);
    }

    // ---------------------------------------------------------------- Conteos generales ----

    [Fact]
    public void Cuenta_recursos_identidades_grupos_y_categorias()
    {
        var filas = new[]
        {
            Fila("vm-1", 100m, 2026, 1, categoria: "Cómputo", rg: "rg-a"),
            Fila("vm-2", 50m, 2026, 1, categoria: "Storage", rg: "rg-a"),
            Fila("vm-1", 100m, 2026, 2, categoria: "Cómputo", rg: "rg-a"),
        };
        var modelo = ConsumoCalculador.Calcular(filas, filasAntesDeFusionar: 3, contexto: Contexto(2026, 1, 2026, 2));

        Assert.Equal(2, modelo!.NumRecursos); // vm-1, vm-2
        Assert.Equal(2, modelo.NumIdentidades); // sub-1|rg-a|vm-1, sub-1|rg-a|vm-2
        Assert.Equal(1, modelo.NumGruposRecursos);
        Assert.Equal(2, modelo.NumCategorias);
        // enero: vm-1 y vm-2 activos -> 2; febrero: solo vm-1 -> 1. El pico es 2, en enero.
        Assert.Equal(2, modelo.PicoRecursosActivos);
        Assert.Equal("2026-01", modelo.MesDePicoActivos);
    }

    [Fact]
    public void Categoria_en_blanco_usa_el_balde_sin_categoria()
    {
        var filas = new[]
        {
            Fila("vm-1", 1000m, 2025, 1, categoria: null),
            Fila("vm-1", 1200m, 2026, 1, categoria: null),
        };
        var modelo = ConsumoCalculador.Calcular(filas, filasAntesDeFusionar: 2, contexto: Contexto(2025, 1, 2026, 1));

        var fila = Assert.Single(modelo!.Comparativa!.Filas);
        Assert.Equal("(sin categoría)", fila[0]);
    }

    // ---------------------------------------------------- Tarea 7, entrega 6: unitario/mom ----
    // Costo por recurso y variación mes contra mes con reducciones/incrementos separados. Las dos
    // reusan agregados que Calcular ya arma para Serie y para D3 (cats): ningún test de acá agrupa
    // facturación por su cuenta, salvo el escenario de 0 recursos activos, que es defensivo (ver
    // el docstring de CalcularCostoUnitario: esa fila no existe con datos reales).

    private static IReadOnlyList<object?> UnitarioDelMes(ConsumoModelo modelo, string mes) =>
        modelo.CostoUnitario.Single(fila => (string)fila[0]! == mes);

    /// <summary>Genera un solo mes con exactamente <paramref name="numRecursos"/> recursos activos
    /// y <paramref name="montoDelMes"/> de facturación total: un recurso factura el monto completo,
    /// el resto factura cero y aun así queda activo (D5/D6: "activo" es "tiene una fila ese mes",
    /// no "facturó más de cero" — ver el docstring de <c>ConstruirSerieAltasYBajas</c>).</summary>
    private static IReadOnlyList<FacturacionRow> MesConNRecursosYMonto(
        int anio, int mes, int numRecursos, decimal montoDelMes)
    {
        var filas = new List<FacturacionRow>();
        for (var i = 0; i < numRecursos; i++)
            filas.Add(Fila($"recurso-{i}", i == 0 ? montoDelMes : 0m, anio, mes));
        return filas;
    }

    /// <summary>
    /// Las dos cifras del HTML de referencia (D.O. de la Tarea 7): 558 recursos/$18,129.29 de
    /// diciembre dan un costo por recurso de 32.49; 912 recursos/$20,896.90 de junio dan 22.91.
    /// Calculado a mano: 18129.29/558=32.4897...-&gt;32.49 (ComoJs); 20896.90/912=22.9132...-&gt;22.91.
    /// </summary>
    [Fact]
    public void Unitario_costo_por_recurso_sale_exacto_con_las_cifras_del_html_de_referencia()
    {
        var modeloDiciembre = ConsumoCalculador.Calcular(
            MesConNRecursosYMonto(2025, 12, 558, 18129.29m), filasAntesDeFusionar: 558,
            contexto: Contexto(2025, 12, 2025, 12, forzados: []));
        var filaDiciembre = UnitarioDelMes(modeloDiciembre!, "2025-12");
        Assert.Equal(558, filaDiciembre[1]);
        Assert.Equal(18129.29m, filaDiciembre[2]);
        Assert.Equal(32.49m, filaDiciembre[3]);
        Assert.Equal(0, filaDiciembre[4]);

        var modeloJunio = ConsumoCalculador.Calcular(
            MesConNRecursosYMonto(2026, 6, 912, 20896.90m), filasAntesDeFusionar: 912,
            contexto: Contexto(2026, 6, 2026, 6, forzados: []));
        var filaJunio = UnitarioDelMes(modeloJunio!, "2026-06");
        Assert.Equal(912, filaJunio[1]);
        Assert.Equal(20896.90m, filaJunio[2]);
        Assert.Equal(22.91m, filaJunio[3]);
    }

    /// <summary>
    /// Guard defensivo: con 0 recursos activos, el costo por recurso es <c>null</c>, nunca una
    /// división por cero. No es reproducible con datos reales llamando a <c>Calcular</c> (una fila
    /// de Serie sin ningún recurso activo ese mes no llega a existir: D0 solo agrega meses que
    /// tienen al menos una fila de facturación, y esa fila ya cuenta como un recurso activo), así
    /// que se prueba con una fila de Serie sintética directamente sobre el método interno.
    /// </summary>
    [Fact]
    public void Unitario_con_cero_recursos_activos_no_divide()
    {
        List<IReadOnlyList<object?>> serieSintetica = [["2026-01", 0, 0, 0, 500m, 0m, 0]];

        var unitario = ConsumoCalculador.CalcularCostoUnitario(serieSintetica);

        var fila = Assert.Single(unitario);
        Assert.Equal(0, fila[1]);
        Assert.Null(fila[3]);
    }

    [Fact]
    public void Unitario_marca_el_mes_parcial_con_flag_1()
    {
        var contexto = Contexto(2026, 1, 2026, 4, forzados: ["2026-04"]);
        var modelo = ConsumoCalculador.Calcular(
            EscenarioMesParcialAlFinal(), filasAntesDeFusionar: 9, contexto: contexto);

        Assert.Equal(1, UnitarioDelMes(modelo!, "2026-04")[4]);
        Assert.Equal(0, UnitarioDelMes(modelo!, "2026-03")[4]); // control: no parcial
    }

    /// <summary>Dos categorías entre dos meses: Backup baja 300 (1000-&gt;700), Redes sube 100
    /// (200-&gt;300). Reducciones=300, incrementos=100, neto=300-100=200 — la fila del segundo mes,
    /// el único que tiene un mes anterior contra el cual comparar.</summary>
    [Fact]
    public void MoM_separa_reducciones_e_incrementos_con_signo_positivo_los_dos()
    {
        var filas = MesesDeCategoria("Backup", 1000m, 700m)
            .Concat(MesesDeCategoria("Redes", 200m, 300m))
            .ToList();
        var modelo = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 4, contexto: Contexto(2026, 1, 2026, 2, forzados: []));

        var fila = Assert.Single(modelo!.VariacionMoM);
        Assert.Equal(["2026-02", 300m, 100m, 200m], fila);
    }

    [Fact]
    public void MoM_el_primer_mes_del_rango_no_produce_fila()
    {
        var filas = MesesDeCategoria("Backup", 1000m, 700m)
            .Concat(MesesDeCategoria("Redes", 200m, 300m))
            .ToList();
        var modelo = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 4, contexto: Contexto(2026, 1, 2026, 2, forzados: []));

        Assert.DoesNotContain(modelo!.VariacionMoM, fila => (string)fila[0]! == "2026-01");
    }

    /// <summary>
    /// Una categoría que no facturó en el mes anterior (Nueva, ausente en enero) cuenta como cero
    /// ese mes, no se ignora: su alta completa (500) entra como incremento. Backup se mantiene
    /// estable (1000 los dos meses) y no aporta nada a ninguna de las dos series.
    /// </summary>
    [Fact]
    public void MoM_una_categoria_ausente_en_el_mes_anterior_cuenta_como_cero()
    {
        var filas = MesesDeCategoria("Backup", 1000m, 1000m)
            .Concat(new[] { Fila("recurso-Nueva", 500m, 2026, 2, categoria: "Nueva") })
            .ToList();
        var modelo = ConsumoCalculador.Calcular(
            filas, filasAntesDeFusionar: 3, contexto: Contexto(2026, 1, 2026, 2, forzados: []));

        var fila = Assert.Single(modelo!.VariacionMoM);
        Assert.Equal(["2026-02", 0m, 500m, -500m], fila);
    }
}
