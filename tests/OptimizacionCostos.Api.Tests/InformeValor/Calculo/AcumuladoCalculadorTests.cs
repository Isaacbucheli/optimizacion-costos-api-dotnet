using System.Globalization;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Tarea 5 del plan de la entrega 6: el acumulado de lo ejecutado, titular del informe. Pura y sin
/// reloj (ver <c>SinRelojDelSistemaTests</c>, que escanea <c>Calculo/</c> completo). El fixture
/// central reconstruye la PPT de referencia al centavo — ver el comentario de
/// <see cref="AcumuladoCalculador"/>.
/// </summary>
public sealed class AcumuladoCalculadorTests
{
    private static int _n;

    // ── Builders ──

    /// <summary>Arma una <see cref="AccionEjecutada"/> con fuente/autoría fijas ("matriz"/
    /// "declarada": no importan para esta calculadora, que ya recibe las filas resueltas). Con
    /// <paramref name="monto"/> null arma una fila "sin monto", con su propio motivo.</summary>
    private static AccionEjecutada F(string oportunidad, string categoria, string mes, decimal? monto, string? fin = null, string? fuenteMonto = null) =>
        new(
            Fuente: "matriz",
            Oportunidad: oportunidad,
            Categoria: categoria,
            SubscriptionId: "s1",
            ResourceGroup: "rg1",
            ResourceName: $"recurso{++_n}",
            MesEjecucion: mes,
            MesFin: fin,
            MontoMensual: monto,
            FuenteMonto: monto is null ? null : (fuenteMonto ?? "facturado"),
            MotivoSinMonto: monto is null ? "sin match de facturación para este recurso" : null,
            Autoria: "declarada");

    private static readonly RegistroEjes EjesOk = new(
        BarridoMedido: true, BarridoMotivo: null, ReservasMedidas: true, ReservasMotivo: null, Indeterminadas: 0);

    private static readonly ReservasFacturadasModelo ReservasVacioMedido = new(
        Medido: true, Motivo: null, Filas: [], TotalDemanda: 0m, TotalReserva: 0m, TotalAhorro: 0m,
        AhorroAnualizado: 0m, SinLineaEnEvolucion: [], ConsumidoresNoLeidos: 0);

    private static ContextoInformeValor Ctx(string inicio, string fin, string corte) =>
        new(
            DateOnly.ParseExact(inicio, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly.ParseExact(fin, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly.ParseExact(corte, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            null);

    // ── La PPT de referencia ──

    /// <summary>El registro reconstruido de la PPT de referencia, verificado al centavo contra los
    /// gráficos embebidos en ella (el análisis vive en los docs internos del proyecto, 2026-08-13).
    /// La calculadora tiene que reproducir la serie acumulada, la tasa de junio y la proyección
    /// corregida. Los montos son reales pero van sin cliente: acá son solo la serie.</summary>
    [Fact]
    public void La_ppt_de_referencia_se_reproduce_al_centavo()
    {
        var filas = new List<AccionEjecutada>
        {
            F("Retiro de réplicas de VMs", "Discos / Réplicas", "2025-12", 106.02m),
            F("Rightsizing de VMs", "VMs (right-size / apagado)", "2026-01", 277.70m),
            F("Eliminación de discos huérfanos", "Discos / Réplicas", "2026-01", 263.14m),
            F("Eliminación de Private Endpoints", "Red (IPs / Endpoints / DNS)", "2026-01", 28.80m),
            F("Eliminación de IPs públicas huérfanas", "Red (IPs / Endpoints / DNS)", "2026-01", 18.00m),
            F("Eliminación de Private DNS Zones", "Red (IPs / Endpoints / DNS)", "2026-02", 1.00m),
            F("Eliminación de App Service huérfano", "App Service", "2026-03", 73.00m),
            F("Rightsizing de VMs", "VMs (right-size / apagado)", "2026-04", 3794.44m),
            F("Eliminación de servidores en desuso", "VMs (right-size / apagado)", "2026-04", 51.08m),
            F("Eliminación de discos huérfanos", "Discos / Réplicas", "2026-04", 9.80m),
            F("Reservas de Microsoft Fabric", "Reservas", "2026-06", 5110.39m, fin: "2027-06"),
            F("Compra de reservas de VMs", "Reservas", "2026-06", 1392.48m, fin: "2027-06"),
            F("Reserva de BD PostgreSQL", "Reservas", "2026-06", 415.52m, fin: "2027-06"),
            F("Eliminación de discos huérfanos", "Discos / Réplicas", "2026-06", 133.15m),
        };
        var m = AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, gastoTotalRango: null,
            Ctx("2025-12-01", "2026-06-30", corte: "2026-06-30"));

        // La serie acumulada de la diapositiva 2, al centavo:
        decimal[] esperado = [106.02m, 799.68m, 1494.34m, 2262.00m, 6884.98m, 11507.96m, 23182.48m];
        Assert.Equal(esperado, m.Serie.Select(f => (decimal)f[2]!).ToArray());
        Assert.Equal(23182.48m, m.AcumuladoTotal);
        Assert.Equal(11674.52m, m.TasaVigenteCierre);
        // La proyección CORRECTA (la PPT decía 93,108: edición a mano, no se reproduce):
        Assert.Equal(93229.60m, m.ProyeccionFinDeAnio);
        // Invariante 2:
        Assert.Equal(m.AcumuladoTotal, m.PorOportunidad.Sum(f => (decimal)f[1]!));
    }

    /// <summary>Igual fixture que la PPT, pero pidiendo el porcentaje sobre el gasto del período:
    /// 23,182.48 / 200,000 × 100 = 11.59124 → 11.6 a un decimal.</summary>
    [Fact]
    public void PctGastoPeriodo_redondea_a_un_decimal_sobre_el_gasto_del_rango()
    {
        var filas = new List<AccionEjecutada> { F("Rightsizing de VMs", "VMs (right-size / apagado)", "2026-01", 23182.48m) };
        var m = AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, gastoTotalRango: 200_000m,
            Ctx("2026-01-01", "2026-01-31", corte: "2026-01-31"));

        Assert.Equal(11.6m, m.PctGastoPeriodo);
    }

    [Fact]
    public void Gasto_del_rango_nulo_o_no_positivo_deja_el_porcentaje_en_null()
    {
        var filas = new List<AccionEjecutada> { F("X", "Cat", "2026-01", 100m) };
        var contexto = Ctx("2026-01-01", "2026-01-31", corte: "2026-01-31");

        Assert.Null(AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, null, contexto).PctGastoPeriodo);
        Assert.Null(AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, 0m, contexto).PctGastoPeriodo);
        Assert.Null(AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, -50m, contexto).PctGastoPeriodo);
    }

    // ── Vigencia: MesFin corta la tasa y la proyección ──

    /// <summary>Fila A vence a fines de febrero (vigente hasta "2026-02" inclusive); fila B no
    /// vence. La tasa de la serie tiene que caer de 150 a 50 exactamente en marzo, y la proyección
    /// (que arranca en abril, corte fuera de diciembre) tiene que seguir en 50, nunca "volver" a
    /// contar la fila vencida.</summary>
    [Fact]
    public void Vencimiento_corta_la_tasa_de_la_serie_y_de_la_proyeccion()
    {
        var filas = new List<AccionEjecutada>
        {
            F("A vence en febrero", "Cat", "2026-01", 100m, fin: "2026-02"),
            F("B no vence", "Cat", "2026-01", 50m),
        };
        var m = AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, null,
            Ctx("2026-01-01", "2026-04-30", corte: "2026-04-30"));

        Assert.Equal([150m, 150m, 50m, 50m], m.Serie.Select(f => (decimal)f[1]!).ToArray());
        Assert.Equal([150m, 300m, 350m, 400m], m.Serie.Select(f => (decimal)f[2]!).ToArray());
        Assert.Equal(400m, m.AcumuladoTotal);
        Assert.Equal(50m, m.TasaVigenteCierre);

        // Proyección: mayo a diciembre (8 meses), siempre a 50 (A ya venció, no vuelve a sumar).
        Assert.Equal(8, m.Proyeccion.Count);
        Assert.All(m.Proyeccion, f => Assert.Equal(50m, (decimal)f[1]!));
        Assert.Equal(400m + 50m * 8, m.ProyeccionFinDeAnio);
    }

    // ── Acción anterior al rango: aporta tasa, no historia ──

    /// <summary>La fila se ejecutó en 2025-01, un año antes del rango (2026-01 a 2026-03). Aporta su
    /// tasa completa desde el primer mes del rango (su ahorro se sigue percibiendo), pero
    /// <c>AcumuladoTotal</c>/<c>PorOportunidad</c> solo cuentan los 3 meses del rango — nunca los 12
    /// meses transcurridos desde su ejecución real.</summary>
    [Fact]
    public void Accion_anterior_al_rango_aporta_tasa_sin_arrastrar_su_historia()
    {
        var filas = new List<AccionEjecutada> { F("Vieja", "Cat", "2025-01", 20m) };
        var m = AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, null,
            Ctx("2026-01-01", "2026-03-31", corte: "2026-03-31"));

        Assert.Equal([20m, 20m, 20m], m.Serie.Select(f => (decimal)f[1]!).ToArray()); // vigente los 3 meses
        Assert.Equal(60m, m.AcumuladoTotal); // 20 × 3 meses DEL RANGO, no 20 × 15 meses desde su ejecución
        Assert.Equal(60m, Assert.Single(m.PorOportunidad)[1]);
    }

    // ── Fila sin monto: no suma, pero cuenta ──

    [Fact]
    public void Fila_sin_monto_no_entra_a_la_suma_pero_cuenta_en_filas_sin_monto()
    {
        var filas = new List<AccionEjecutada>
        {
            F("Sin match", "Cat", "2026-01", null),
            F("Con monto", "Cat", "2026-01", 30m),
        };
        var m = AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, null,
            Ctx("2026-01-01", "2026-02-28", corte: "2026-02-28"));

        Assert.Equal(1, m.FilasSinMonto);
        Assert.Equal(2, m.Filas.Count); // la fila sin monto sigue publicada, fuera de la aritmética
        Assert.Equal([30m, 30m], m.Serie.Select(f => (decimal)f[1]!).ToArray());
        Assert.Equal(60m, m.AcumuladoTotal);
    }

    // ── Corte en diciembre: proyección vacía ──

    [Fact]
    public void Corte_en_diciembre_deja_la_proyeccion_vacia_y_el_fin_de_anio_es_el_acumulado()
    {
        var filas = new List<AccionEjecutada> { F("Única", "Cat", "2026-12", 10m) };
        var m = AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, null,
            Ctx("2026-12-01", "2026-12-31", corte: "2026-12-31"));

        Assert.Equal(10m, m.AcumuladoTotal);
        Assert.Empty(m.Proyeccion);
        Assert.Equal(10m, m.ProyeccionFinDeAnio);
    }

    // ── Invariante 1: identidad exacta, con un vencimiento a mitad de rango ──

    /// <summary>Fila F vence a fines de marzo (mitad del rango de 6 meses); fila G no vence. La
    /// identidad se verifica dos veces: mes a mes (la diferencia entre acumulados consecutivos es
    /// exactamente la tasa vigente de ese mes) y de punta a punta (el acumulado final es la suma,
    /// por fila, de monto × meses vigentes dentro del rango — calculado acá de forma independiente
    /// de la calculadora, a mano).</summary>
    [Fact]
    public void Invariante_1_identidad_exacta_con_vencimiento_a_mitad_de_rango()
    {
        var filas = new List<AccionEjecutada>
        {
            F("F vence en marzo", "Cat", "2026-01", 40m, fin: "2026-03"),
            F("G no vence", "Cat", "2026-01", 10m),
        };
        var m = AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, null,
            Ctx("2026-01-01", "2026-06-30", corte: "2026-06-30"));

        var serie = m.Serie;
        Assert.Equal(6, serie.Count);

        var acumuladoPrevio = 0m;
        foreach (var fila in serie)
        {
            var tasa = (decimal)fila[1]!;
            var acumulado = (decimal)fila[2]!;
            Assert.Equal(tasa, acumulado - acumuladoPrevio); // acumulado(m) - acumulado(m-1) == tasaVigente(m)
            acumuladoPrevio = acumulado;
        }

        // F vigente en ene/feb/mar (3 meses del rango); G vigente los 6 meses del rango.
        var esperadoDePuntaAPunta = 40m * 3 + 10m * 6;
        Assert.Equal(esperadoDePuntaAPunta, m.AcumuladoTotal);
        Assert.Equal(esperadoDePuntaAPunta, acumuladoPrevio); // el último acumulado de la serie es el total
    }

    // ── Invariante 2: composición facturado/estimado ──

    /// <summary>La composición facturado + estimado es exactamente el acumulado total, porque ambos
    /// usan la misma función de contribución que <c>PorOportunidad</c>. Verificado con un fixture que
    /// tiene tanto filas facturadas como estimadas.</summary>
    [Fact]
    public void Invariante_2_composicion_facturado_mas_estimado_es_acumulado_total()
    {
        var filas = new List<AccionEjecutada>
        {
            F("A facturada", "Cat", "2026-01", 100m, fuenteMonto: "facturado"),
            F("B facturada", "Cat", "2026-02", 50m, fuenteMonto: "facturado"),
            F("C estimada", "Cat", "2026-01", 75m, fuenteMonto: "estimado"),
            F("D estimada", "Cat", "2026-03", 25m, fuenteMonto: "estimado"),
        };
        var m = AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, null,
            Ctx("2026-01-01", "2026-03-31", corte: "2026-03-31"));

        // Esperado: A(100×3) + B(50×2) + C(75×3) + D(25×1) = 300+100+225+25 = 650
        // Facturado: A(300) + B(100) = 400
        // Estimado: C(225) + D(25) = 250
        Assert.Equal(650m, m.AcumuladoTotal);
        Assert.Equal(400m, m.MontoFacturado);
        Assert.Equal(250m, m.MontoEstimado);
        Assert.Equal(m.AcumuladoTotal, m.MontoFacturado + m.MontoEstimado);
    }

    /// <summary>Fila con monto pero FuenteMonto no reconocida ("otra-cosa" en vez de "facturado"
    /// o "estimado") dispara una excepción con un mensaje descriptivo — no falla silenciosamente
    /// dejando la fila fuera de ambas composiciones.</summary>
    [Fact]
    public void Fila_con_monto_y_fuente_no_reconocida_lanza_excepcion()
    {
        var filas = new List<AccionEjecutada>
        {
            new AccionEjecutada(
                Fuente: "matriz",
                Oportunidad: "Mala fuente",
                Categoria: "Cat",
                SubscriptionId: "s1",
                ResourceGroup: "rg1",
                ResourceName: "recurso-x",
                MesEjecucion: "2026-01",
                MesFin: null,
                MontoMensual: 100m,
                FuenteMonto: "otra-cosa", // inválida
                MotivoSinMonto: null,
                Autoria: "matriz"),
        };
        var contexto = Ctx("2026-01-01", "2026-01-31", corte: "2026-01-31");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, null, contexto));

        Assert.Contains("Fila con monto sin fuente reconocida", ex.Message);
        Assert.Contains("'otra-cosa'", ex.Message);
        Assert.Contains("Mala fuente", ex.Message);
    }

    // ── I1 del review final: medido y motivo son independientes ──

    [Fact]
    public void Con_todo_medido_el_motivo_es_null()
    {
        var filas = new List<AccionEjecutada> { F("X", "Cat", "2026-01", 100m) };
        var m = AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, null,
            Ctx("2026-01-01", "2026-01-31", corte: "2026-01-31"));

        Assert.True(m.Medido);
        Assert.Null(m.Motivo);
    }

    /// <summary>El caso del brief: reservas medidas, barrido sin medir. <c>Medido</c> sigue en
    /// <c>true</c> (las reservas alcanzan para que el conjunto mida algo), pero <c>Motivo</c> ya no
    /// es <c>null</c> solo porque el conjunto midió: declara el eje que falló.</summary>
    [Fact]
    public void Barrido_no_medido_con_reservas_medidas_da_medido_true_y_motivo_declara_lo_que_falto()
    {
        var ejes = new RegistroEjes(
            BarridoMedido: false, BarridoMotivo: "El cliente no tiene ningún barrido de optimización corrido.",
            ReservasMedidas: true, ReservasMotivo: null, Indeterminadas: 0);

        var m = AcumuladoCalculador.Calcular([], ejes, ReservasVacioMedido, null,
            Ctx("2026-01-01", "2026-01-31", corte: "2026-01-31"));

        Assert.True(m.Medido);
        Assert.NotNull(m.Motivo);
        Assert.Contains("El cliente no tiene ningún barrido de optimización corrido.", m.Motivo);
    }

    // ── I2 del review final: corte anterior al fin del rango no duplica meses ──

    /// <summary>El rango cubre enero a junio (<c>MesesDelRango</c> depende de <c>PeriodEnd</c>,
    /// nunca del corte), pero el corte cae en marzo. Sin el fix, la proyección arrancaba en abril y
    /// contaba abril/mayo/junio dos veces (una en la serie histórica, otra en la proyección).</summary>
    [Fact]
    public void Corte_anterior_al_fin_del_periodo_la_proyeccion_arranca_despues_del_rango_no_del_corte()
    {
        var filas = new List<AccionEjecutada> { F("Única", "Cat", "2026-01", 50m) };
        var m = AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, null,
            Ctx("2026-01-01", "2026-06-30", corte: "2026-03-31"));

        // La serie histórica cubre los 6 meses del RANGO, pese a que el corte cae en marzo.
        Assert.Equal(6, m.Serie.Count);
        Assert.Equal(300m, m.AcumuladoTotal); // 50 x 6 meses

        // La proyección arranca en julio (el mes siguiente al FIN DEL RANGO), no en abril.
        Assert.Equal(6, m.Proyeccion.Count); // julio a diciembre
        Assert.Equal("2026-07", m.Proyeccion[0][0]);
        Assert.Equal(300m + 50m * 6, m.ProyeccionFinDeAnio);
    }

    /// <summary>Entrega 8, pieza B: el monto declarado es la tercera componente de la composición
    /// y la invariante de suma se mantiene a tres fuentes.</summary>
    [Fact]
    public void La_composicion_declarada_cuadra_con_el_total_a_tres_fuentes()
    {
        var filas = new List<AccionEjecutada>
        {
            F("Delta medido", "VMs (right-size / apagado)", "2026-01", 100m, fuenteMonto: "facturado"),
            F("Estimado del barrido", "Discos / Réplicas", "2026-01", 100m, fuenteMonto: "estimado"),
            F("Apagado declarado a mano", "(sin categoría)", "2026-01", 100m, fuenteMonto: "declarado"),
        };
        var m = AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, gastoTotalRango: null,
            Ctx("2026-01-01", "2026-01-31", corte: "2026-01-31"));

        Assert.Equal(100m, m.MontoFacturado);
        Assert.Equal(100m, m.MontoEstimado);
        Assert.Equal(100m, m.MontoDeclarado);
        Assert.Equal(m.AcumuladoTotal, m.MontoFacturado + m.MontoEstimado + m.MontoDeclarado);
    }

    /// <summary>Entrega 8: una fila SinProyeccion (reserva heredada del respaldo, sin vencimiento
    /// derivable) suma su tasa dentro del rango —es un hecho facturado— pero la proyección a fin
    /// de año la excluye: la salvaguarda 4 pesa más que la cifra.</summary>
    [Fact]
    public void Las_filas_sin_proyeccion_suman_en_el_rango_pero_no_proyectan()
    {
        var filas = new List<AccionEjecutada>
        {
            F("Reserva Standard_B4ms (1 Year)", "Reservas", "2026-01", 100m, fuenteMonto: "estimado")
                with { SinProyeccion = true },
        };
        var m = AcumuladoCalculador.Calcular(filas, EjesOk, ReservasVacioMedido, gastoTotalRango: null,
            Ctx("2026-01-01", "2026-03-31", corte: "2026-03-31"));

        Assert.Equal(300m, m.AcumuladoTotal); // tasa 100 vigente los 3 meses del rango
        Assert.Equal(9, m.Proyeccion.Count);  // abril a diciembre
        Assert.All(m.Proyeccion, p => Assert.Equal(0m, (decimal)p[1]!)); // tasa proyectada cero
        Assert.Equal(300m, m.ProyeccionFinDeAnio); // el acumulado no crece a futuro
    }
}
