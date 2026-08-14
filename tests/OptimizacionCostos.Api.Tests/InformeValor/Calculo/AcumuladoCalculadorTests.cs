using System.Globalization;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Tarea 5 del plan de la entrega 6: el acumulado de lo ejecutado, titular del informe. Pura y sin
/// reloj (ver <c>SinRelojDelSistemaTests</c>, que escanea <c>Calculo/</c> completo). El fixture
/// central reconstruye la PPT de MERCANTIL al centavo — ver el comentario de
/// <see cref="AcumuladoCalculador"/>.
/// </summary>
public sealed class AcumuladoCalculadorTests
{
    private static int _n;

    // ── Builders ──

    /// <summary>Arma una <see cref="AccionEjecutada"/> con fuente/autoría fijas ("matriz"/
    /// "declarada": no importan para esta calculadora, que ya recibe las filas resueltas). Con
    /// <paramref name="monto"/> null arma una fila "sin monto", con su propio motivo.</summary>
    private static AccionEjecutada F(string oportunidad, string categoria, string mes, decimal? monto, string? fin = null) =>
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
            FuenteMonto: monto is null ? null : "facturado",
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

    // ── La PPT de MERCANTIL ──

    /// <summary>El registro reconstruido de la PPT de MERCANTIL, verificado al centavo contra sus
    /// gráficos embebidos (docs/2026-08-13-analisis-ppt-mercantil-informe-valor.md). La calculadora
    /// tiene que reproducir la serie acumulada, la tasa de junio y la proyección corregida.</summary>
    [Fact]
    public void La_ppt_de_mercantil_se_reproduce_al_centavo()
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
}
