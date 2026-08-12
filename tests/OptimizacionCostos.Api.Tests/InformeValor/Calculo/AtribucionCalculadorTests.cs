using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Tareas 3 y 4 del plan de la entrega 2d (E1, E3, E4, E6). Todos los escenarios usan una ventana
/// mínima de 6 meses no parciales (base = meses 1-3, cierre = meses 4-6, igual que
/// <c>ConsumoCalculador.CalcularAhorro</c>): con menos historia la función devuelve <c>null</c> antes
/// de clasificar nada. Datos sintéticos en todos los casos (regla dura del encargo: nada de nombres
/// de recurso ni montos de cliente reales, ni siquiera los que aparecen citados en el plan).
/// </summary>
public sealed class AtribucionCalculadorTests
{
    private static readonly HashSet<string> SinReservas = [];
    private static int _hash;

    private static FacturacionRow Fila(string recurso, decimal pvp, int mes, string subscriptionId, string rg) =>
        new(Hash: $"h{++_hash}", Tenant: null, SubscriptionName: $"Nombre-{subscriptionId}", SubscriptionId: subscriptionId,
            ResourceGroup: rg, ResourceName: recurso, CostCenter: null, Category: "Cómputo",
            Subcategory: null, Service: null, Quantity: null, Unit: null, Rate: null,
            Pvp: pvp, Year: 2026, Month: (byte)mes);

    /// <summary>Filas de un recurso solo en los meses declarados: un mes ausente es "no facturó ese
    /// mes", no cero explícito — igual que un export real, donde un recurso apagado simplemente no
    /// tiene fila.</summary>
    private static IReadOnlyList<FacturacionRow> Meses(
        string recurso, string subscriptionId, string rg, params (int Mes, decimal Monto)[] valores) =>
        valores.Select(v => Fila(recurso, v.Monto, v.Mes, subscriptionId, rg)).ToList();

    private static ContextoInformeValor Contexto(int inicioMes, int finMes) => new(
        new DateOnly(2026, inicioMes, 1),
        new DateOnly(2026, finMes, DateTime.DaysInMonth(2026, finMes)),
        Corte: new DateOnly(2026, finMes, DateTime.DaysInMonth(2026, finMes)),
        MesesParcialesForzados: null);

    private static HallazgoResueltoFila Hallazgo(
        string subscriptionId, string rg, string recurso, DateOnly? resueltoEl,
        string? matrixCode = "1.1", string hallazgo = "Hallazgo de prueba", int pilar = 1) =>
        new(subscriptionId, $"Nombre-{subscriptionId}", rg, recurso, resueltoEl, matrixCode, hallazgo, pilar);

    private static string Id(string subscriptionId, string rg, string recurso) => $"{subscriptionId}|{rg}|{recurso}";

    // --------------------------------------------------------------- Nulo ----

    [Fact]
    public void Sin_filas_en_el_rango_devuelve_null()
    {
        var filas = Meses("vm-1", "sub-1", "rg-1", (1, 100m), (2, 100m), (3, 100m));

        var modelo = AtribucionCalculador.Calcular(
            filas, [], mesesParciales: [], SinReservas, Contexto(inicioMes: 6, finMes: 6));

        Assert.Null(modelo);
    }

    /// <summary>Mismo mínimo que D3 (tres meses de base más tres de cierre): con 5 meses no
    /// parciales no hay suficiente historia para que "ventana base" signifique algo.</summary>
    [Fact]
    public void Menos_de_seis_meses_no_parciales_devuelve_null()
    {
        var filas = Meses("vm-1", "sub-1", "rg-1", (1, 100m), (2, 100m), (3, 100m), (4, 50m), (5, 50m));

        var modelo = AtribucionCalculador.Calcular(
            filas, [], mesesParciales: [], SinReservas, Contexto(1, 5));

        Assert.Null(modelo);
    }

    // ------------------------------------------------- Los cuatro mecanismos ----

    [Fact]
    public void Dejo_de_facturar_recurso_con_promedio_base_positivo_y_cierre_en_cero()
    {
        var filas = Meses("vm-1", "sub-1", "rg-1", (1, 100m), (2, 100m), (3, 100m)); // nada en 4-6
        // Se necesita otro recurso con datos en 4-6 para que la ventana de cierre exista.
        var relleno = Meses("vm-ancla", "sub-1", "rg-1", (1, 1m), (2, 1m), (3, 1m), (4, 1m), (5, 1m), (6, 1m));

        var modelo = AtribucionCalculador.Calcular(
            filas.Concat(relleno).ToList(), [], [], SinReservas, Contexto(1, 6));

        Assert.NotNull(modelo);
        Assert.Equal(1, modelo!.SinAtribuir.DejoDeFacturar.Cantidad);
        Assert.Equal(100m, modelo.SinAtribuir.DejoDeFacturar.Total);
        var recurso = Assert.Single(modelo.SinAtribuir.DejoDeFacturar.Recursos, r => r.ResourceName == "vm-1");
        Assert.Equal(100m, recurso.BaseAvg);
        Assert.Equal(0m, recurso.FinAvg);
    }

    [Fact]
    public void Recurso_nuevo_sin_promedio_base_y_con_cierre_positivo()
    {
        var filas = Meses("vm-1", "sub-1", "rg-1", (4, 60m), (5, 60m), (6, 60m)); // nada en 1-3
        var relleno = Meses("vm-ancla", "sub-1", "rg-1", (1, 1m), (2, 1m), (3, 1m), (4, 1m), (5, 1m), (6, 1m));

        var modelo = AtribucionCalculador.Calcular(
            filas.Concat(relleno).ToList(), [], [], SinReservas, Contexto(1, 6));

        Assert.Equal(1, modelo!.SinAtribuir.Nuevo.Cantidad);
        Assert.Equal(-60m, modelo.SinAtribuir.Nuevo.Total); // convencion de signo: negativo = el gasto subio
    }

    [Fact]
    public void Vivo_cuesta_menos_recurso_con_las_dos_ventanas_positivas_y_cierre_menor()
    {
        var filas = Meses("vm-1", "sub-1", "rg-1", (1, 200m), (2, 200m), (3, 200m), (4, 80m), (5, 80m), (6, 80m));

        var modelo = AtribucionCalculador.Calcular(filas, [], [], SinReservas, Contexto(1, 6));

        Assert.Equal(1, modelo!.SinAtribuir.VivoCuestaMenos.Cantidad);
        Assert.Equal(120m, modelo.SinAtribuir.VivoCuestaMenos.Total);
    }

    [Fact]
    public void Vivo_cuesta_mas_recurso_con_las_dos_ventanas_positivas_y_cierre_mayor()
    {
        var filas = Meses("vm-1", "sub-1", "rg-1", (1, 50m), (2, 50m), (3, 50m), (4, 90m), (5, 90m), (6, 90m));

        var modelo = AtribucionCalculador.Calcular(filas, [], [], SinReservas, Contexto(1, 6));

        Assert.Equal(1, modelo!.SinAtribuir.VivoCuestaMas.Cantidad);
        Assert.Equal(-40m, modelo.SinAtribuir.VivoCuestaMas.Total); // negativo: el gasto subio
    }

    /// <summary>Empate exacto (cierre == base, delta = 0): por exclusión cae en "cuesta menos", no
    /// en "cuesta más" (un empate no subió). Arbitrario pero determinístico y sin efecto en la
    /// invariante: aporta cero a cualquiera de los dos lados.</summary>
    [Fact]
    public void Un_empate_exacto_entre_base_y_cierre_cae_en_cuesta_menos_no_en_cuesta_mas()
    {
        var filas = Meses("vm-1", "sub-1", "rg-1", (1, 100m), (2, 100m), (3, 100m), (4, 100m), (5, 100m), (6, 100m));

        var modelo = AtribucionCalculador.Calcular(filas, [], [], SinReservas, Contexto(1, 6));

        Assert.Equal(1, modelo!.SinAtribuir.VivoCuestaMenos.Cantidad);
        Assert.Equal(0m, modelo.SinAtribuir.VivoCuestaMenos.Total);
        Assert.Equal(0, modelo.SinAtribuir.VivoCuestaMas.Cantidad);
    }

    [Fact]
    public void Un_recurso_que_solo_facturo_en_un_mes_parcial_excluido_no_cae_en_ningun_balde()
    {
        // vm-1 solo tiene una fila, en el mes 7: si ese mes se marca parcial, la ventana analizable
        // (meses 1-6, todos no parciales) no lo ve nunca facturando: ni base ni cierre tienen señal.
        var filas = Meses("vm-1", "sub-1", "rg-1", (7, 999m));
        var relleno = Meses("vm-ancla", "sub-1", "rg-1", (1, 1m), (2, 1m), (3, 1m), (4, 1m), (5, 1m), (6, 1m), (7, 1m));

        var modelo = AtribucionCalculador.Calcular(
            filas.Concat(relleno).ToList(), [], mesesParciales: ["2026-07"], SinReservas, Contexto(1, 7));

        Assert.DoesNotContain(modelo!.SinAtribuir.DejoDeFacturar.Recursos, r => r.ResourceName == "vm-1");
        Assert.DoesNotContain(modelo.SinAtribuir.Nuevo.Recursos, r => r.ResourceName == "vm-1");
        Assert.DoesNotContain(modelo.SinAtribuir.VivoCuestaMenos.Recursos, r => r.ResourceName == "vm-1");
        Assert.DoesNotContain(modelo.SinAtribuir.VivoCuestaMas.Recursos, r => r.ResourceName == "vm-1");
    }

    /// <summary>D5, aplicado acá: sin excluir el mes parcial, vm-ancla (que sí factura en el mes 7)
    /// tendría un promedio de cierre artificialmente bajo si el mes 7 (todavía sin cerrar) entrara a
    /// la ventana. Con "2026-07" declarado parcial, la ventana de cierre sigue siendo meses 4-6.
    /// </summary>
    [Fact]
    public void El_mes_parcial_no_entra_a_ninguna_ventana_aunque_tenga_filas()
    {
        var filas = Meses("vm-ancla", "sub-1", "rg-1",
            (1, 100m), (2, 100m), (3, 100m), (4, 100m), (5, 100m), (6, 100m), (7, 1m));

        var modelo = AtribucionCalculador.Calcular(
            filas, [], mesesParciales: ["2026-07"], SinReservas, Contexto(1, 7));

        var recurso = Assert.Single(modelo!.SinAtribuir.VivoCuestaMenos.Recursos.Concat(modelo.SinAtribuir.VivoCuestaMas.Recursos));
        Assert.Equal(100m, recurso.FinAvg); // no 67.33: el mes 7 (parcial, con 1) no entra al promedio
    }

    // ------------------------------------------------------- Balde 2 (E3) ----

    [Fact]
    public void Un_recurso_con_hallazgo_resuelto_dentro_del_periodo_no_cae_en_ningun_mecanismo()
    {
        var filas = Meses("vm-fix", "sub-1", "rg-1", (1, 100m), (2, 100m), (3, 100m), (4, 40m), (5, 40m), (6, 40m));
        var hallazgos = new[] { Hallazgo("sub-1", "rg-1", "vm-fix", new DateOnly(2026, 4, 15)) };

        var modelo = AtribucionCalculador.Calcular(filas, hallazgos, [], SinReservas, Contexto(1, 6));

        Assert.Equal(1, modelo!.PorRecomendacion.Cantidad);
        Assert.Equal(60m, modelo.PorRecomendacion.Total);
        Assert.Equal(0, modelo.SinAtribuir.VivoCuestaMenos.Cantidad); // no se cuenta dos veces
        Assert.Equal(0m, modelo.SinAtribuir.Total);
        Assert.Equal(60m, modelo.VariacionTotal);
        var recurso = Assert.Single(modelo.PorRecomendacion.Recursos);
        Assert.Equal("1.1: Hallazgo de prueba", Assert.Single(recurso.Recomendaciones));
    }

    [Fact]
    public void Un_hallazgo_resuelto_fuera_del_periodo_no_cuenta_y_el_recurso_cae_en_su_mecanismo()
    {
        var filas = Meses("vm-fix", "sub-1", "rg-1", (1, 100m), (2, 100m), (3, 100m), (4, 40m), (5, 40m), (6, 40m));
        var hallazgos = new[] { Hallazgo("sub-1", "rg-1", "vm-fix", new DateOnly(2025, 12, 1)) }; // antes del rango

        var modelo = AtribucionCalculador.Calcular(filas, hallazgos, [], SinReservas, Contexto(1, 6));

        Assert.Equal(0, modelo!.PorRecomendacion.Cantidad);
        Assert.Equal(1, modelo.SinAtribuir.VivoCuestaMenos.Cantidad);
        Assert.Equal(60m, modelo.SinAtribuir.VivoCuestaMenos.Total);
    }

    [Fact]
    public void Un_hallazgo_resuelto_sin_fecha_no_cuenta()
    {
        var filas = Meses("vm-fix", "sub-1", "rg-1", (1, 100m), (2, 100m), (3, 100m), (4, 40m), (5, 40m), (6, 40m));
        var hallazgos = new[] { Hallazgo("sub-1", "rg-1", "vm-fix", resueltoEl: null) };

        var modelo = AtribucionCalculador.Calcular(filas, hallazgos, [], SinReservas, Contexto(1, 6));

        Assert.Equal(0, modelo!.PorRecomendacion.Cantidad);
        Assert.Equal(1, modelo.SinAtribuir.VivoCuestaMenos.Cantidad);
    }

    [Fact]
    public void Un_hallazgo_resuelto_de_otro_recurso_no_contamina_al_que_se_pregunta()
    {
        var filas = Meses("vm-fix", "sub-1", "rg-1", (1, 100m), (2, 100m), (3, 100m), (4, 40m), (5, 40m), (6, 40m));
        var hallazgos = new[] { Hallazgo("sub-1", "rg-1", "otro-recurso", new DateOnly(2026, 4, 15)) };

        var modelo = AtribucionCalculador.Calcular(filas, hallazgos, [], SinReservas, Contexto(1, 6));

        Assert.Equal(0, modelo!.PorRecomendacion.Cantidad);
        Assert.Equal(1, modelo.SinAtribuir.VivoCuestaMenos.Cantidad);
    }

    /// <summary>E3: "cuando un recurso cae en los dos baldes... gana la reserva, y se deja
    /// anotado". El punto de encuentro con la tarea de reservas: <c>recursosConReservaConfirmada</c>
    /// usa el mismo formato de id que D11 (<c>subscriptionId + "|" + resourceGroup + "|" + resourceName</c>).
    /// </summary>
    [Fact]
    public void La_reserva_gana_sobre_la_recomendacion_resuelta_y_el_recurso_queda_anotado()
    {
        var filas = Meses("vm-fix", "sub-1", "rg-1", (1, 100m), (2, 100m), (3, 100m), (4, 40m), (5, 40m), (6, 40m));
        var hallazgos = new[] { Hallazgo("sub-1", "rg-1", "vm-fix", new DateOnly(2026, 4, 15)) };
        var reservados = new HashSet<string> { Id("sub-1", "rg-1", "vm-fix") };

        var modelo = AtribucionCalculador.Calcular(filas, hallazgos, [], reservados, Contexto(1, 6));

        Assert.Equal(0, modelo!.PorRecomendacion.Cantidad);
        Assert.Equal(0, modelo.SinAtribuir.VivoCuestaMenos.Cantidad);
        var anotado = Assert.Single(modelo.ExcluidosPorReserva);
        Assert.Equal("vm-fix", anotado.ResourceName);
        Assert.Equal(60m, anotado.Delta);
        // El recurso no aporta a NINGUNO de los baldes propios de esta calculadora: el balde de
        // reservas es quien se queda con esos 60 al ensamblar el informe completo.
        Assert.Equal(0m, modelo.VariacionTotal);
    }

    [Fact]
    public void Un_recurso_con_reserva_confirmada_pero_sin_hallazgo_tambien_queda_excluido()
    {
        var filas = Meses("vm-1", "sub-1", "rg-1", (1, 100m), (2, 100m), (3, 100m), (4, 40m), (5, 40m), (6, 40m));
        var reservados = new HashSet<string> { Id("sub-1", "rg-1", "vm-1") };

        var modelo = AtribucionCalculador.Calcular(filas, [], [], reservados, Contexto(1, 6));

        Assert.Equal(0, modelo!.SinAtribuir.VivoCuestaMenos.Cantidad);
        Assert.Single(modelo.ExcluidosPorReserva);
    }

    // ----------------------------------------- La invariante (E1), el caso clave ----

    /// <summary>
    /// El requisito central de las dos tareas: los baldes (recomendación + los cuatro mecanismos de
    /// "sin atribuir") suman la variación total, al centavo, con los cuatro mecanismos presentes a
    /// la vez. Números elegidos para poder verificarlos a mano:
    ///
    /// <list type="bullet">
    /// <item>vm-dropped: base 100,100,100 (prom 100), cierre nada (prom 0) → delta +100 (dejó de facturar)</item>
    /// <item>vm-cheaper: base 200×3 (prom 200), cierre 80×3 (prom 80) → delta +120 (vivo, cuesta menos)</item>
    /// <item>vm-pricier: base 50×3 (prom 50), cierre 90×3 (prom 90) → delta −40 (vivo, cuesta más)</item>
    /// <item>vm-new: base nada (prom 0), cierre 70×3 (prom 70) → delta −70 (nuevo)</item>
    /// <item>vm-fixed: base 300×3 (prom 300), cierre 120×3 (prom 120) → delta +180, con hallazgo
    /// resuelto en abril → balde 2, no mecanismo</item>
    /// </list>
    ///
    /// Variación total = 100 + 120 − 40 − 70 + 180 = 290. Verificado también por la vía
    /// independiente (sin pasar por ningún balde): promedio base del portafolio 100+200+50+0+300=650,
    /// promedio de cierre 0+80+90+70+120=360, 650−360=290. Coinciden.
    /// </summary>
    [Fact]
    public void La_invariante_los_baldes_mas_el_crecimiento_suman_la_variacion_total_con_los_cuatro_mecanismos_a_la_vez()
    {
        var filas = Meses("vm-dropped", "sub-1", "rg-1", (1, 100m), (2, 100m), (3, 100m))
            .Concat(Meses("vm-cheaper", "sub-1", "rg-1", (1, 200m), (2, 200m), (3, 200m), (4, 80m), (5, 80m), (6, 80m)))
            .Concat(Meses("vm-pricier", "sub-1", "rg-1", (1, 50m), (2, 50m), (3, 50m), (4, 90m), (5, 90m), (6, 90m)))
            .Concat(Meses("vm-new", "sub-1", "rg-1", (4, 70m), (5, 70m), (6, 70m)))
            .Concat(Meses("vm-fixed", "sub-1", "rg-1", (1, 300m), (2, 300m), (3, 300m), (4, 120m), (5, 120m), (6, 120m)))
            .ToList();
        var hallazgos = new[] { Hallazgo("sub-1", "rg-1", "vm-fixed", new DateOnly(2026, 4, 20)) };

        var modelo = AtribucionCalculador.Calcular(filas, hallazgos, [], SinReservas, Contexto(1, 6));

        Assert.NotNull(modelo);
        Assert.Equal(180m, modelo!.PorRecomendacion.Total);
        Assert.Equal(100m, modelo.SinAtribuir.DejoDeFacturar.Total);
        Assert.Equal(120m, modelo.SinAtribuir.VivoCuestaMenos.Total);
        Assert.Equal(-40m, modelo.SinAtribuir.VivoCuestaMas.Total);
        Assert.Equal(-70m, modelo.SinAtribuir.Nuevo.Total);
        Assert.Equal(110m, modelo.SinAtribuir.Total); // 100+120-40-70
        Assert.Equal(110m, modelo.Crecimiento); // -(-40-70)

        // LA INVARIANTE: recomendación + los cuatro mecanismos, sumados uno por uno, dan la
        // variación total exacta. Sin agrupar por SinAtribuir.Total, para que el test no dependa de
        // que ese campo esté bien calculado también.
        var sumaDeLosCincoBaldes = modelo.PorRecomendacion.Total
            + modelo.SinAtribuir.DejoDeFacturar.Total
            + modelo.SinAtribuir.VivoCuestaMenos.Total
            + modelo.SinAtribuir.VivoCuestaMas.Total
            + modelo.SinAtribuir.Nuevo.Total;
        Assert.Equal(290m, sumaDeLosCincoBaldes);
        Assert.Equal(sumaDeLosCincoBaldes, modelo.VariacionTotal);

        // Contra el cálculo independiente (portafolio completo, sin pasar por ningún mecanismo):
        // 650 de promedio base menos 360 de promedio de cierre.
        Assert.Equal(290m, modelo.VariacionTotal);
    }

    /// <summary>
    /// E1: por qué "redondear cada balde una vez y sumar los baldes ya redondeados" no es lo mismo
    /// que "sumar los deltas crudos y redondear una sola vez al final" — y por qué la calculadora
    /// usa la primera. vm-a aporta un delta crudo de 1.005 (redondea a 1.01, +0.005) a "dejó de
    /// facturar"; vm-b aporta 2.005 (redondea a 2.01, +0.005) a "vivo, cuesta menos". Sumados YA
    /// redondeados: 1.01+2.01=3.02. Sumados crudos y redondeados una sola vez al final: 1.005+2.005=
    /// 3.010, que redondea a 3.01 (el "3.010" no es un empate al centavo, así que no hay que
    /// redondear para arriba ahí). 3.02 ≠ 3.01: por eso el orden de las operaciones no es un
    /// detalle, es la diferencia entre que la suma de los baldes cuadre o no.
    /// </summary>
    [Fact]
    public void E1_el_total_de_sin_atribuir_es_la_suma_de_baldes_ya_redondeados_no_la_suma_cruda_redondeada_al_final()
    {
        var filas = Meses("vm-a", "sub-1", "rg-1", (1, 1.005m), (2, 1.005m), (3, 1.005m))
            .Concat(Meses("vm-b", "sub-1", "rg-1", (1, 12.005m), (2, 12.005m), (3, 12.005m), (4, 10m), (5, 10m), (6, 10m)))
            .ToList();

        var modelo = AtribucionCalculador.Calcular(filas, [], [], SinReservas, Contexto(1, 6));

        Assert.Equal(1.01m, modelo!.SinAtribuir.DejoDeFacturar.Total);
        Assert.Equal(2.01m, modelo.SinAtribuir.VivoCuestaMenos.Total);
        Assert.Equal(3.02m, modelo.SinAtribuir.Total); // 1.01 + 2.01, NO Redondeo.ComoJs(1.005+2.005)=3.01
    }

    // ------------------------------------------------------------------ E6 ----

    /// <summary>
    /// E6: la terna evita que un homónimo que muere en una suscripción y otro que nace en otra se
    /// vean como un solo recurso que "cambió de precio". "vm1" en sub-A/rg-A factura 100 los
    /// primeros tres meses y se apaga; un "vm1" DISTINTO en sub-B/rg-B (mismo nombre, terna
    /// distinta) no factura nada hasta el mes 4 y ahí arranca a 60. Nunca coexistieron.
    /// </summary>
    [Fact]
    public void E6_la_terna_evita_que_un_homonimo_que_muere_y_otro_que_nace_se_vean_como_uno_solo()
    {
        var filas = Meses("vm1", "sub-A", "rg-A", (1, 100m), (2, 100m), (3, 100m))
            .Concat(Meses("vm1", "sub-B", "rg-B", (4, 60m), (5, 60m), (6, 60m)))
            .ToList();

        var modelo = AtribucionCalculador.Calcular(filas, [], [], SinReservas, Contexto(1, 6));

        // Con la terna (código de producción): dos recursos distintos, cada uno en su mecanismo real.
        Assert.Equal(1, modelo!.SinAtribuir.DejoDeFacturar.Cantidad);
        Assert.Equal(100m, modelo.SinAtribuir.DejoDeFacturar.Total);
        Assert.Equal(1, modelo.SinAtribuir.Nuevo.Cantidad);
        Assert.Equal(-60m, modelo.SinAtribuir.Nuevo.Total);
        // Cero: con la terna, los dos homónimos nunca coexisten, así que ninguno pasa por "vivo".
        Assert.Equal(0, modelo.SinAtribuir.VivoCuestaMenos.Cantidad);
        Assert.Equal(0, modelo.SinAtribuir.VivoCuestaMas.Cantidad);

        // Contraste deliberado (NO es código de producción: esta calculadora siempre usa la terna).
        // Si se agrupara solo por nombre, "vm1" sería UN recurso con la serie combinada
        // [100,100,100,60,60,60]: promedio base 100, de cierre 60, delta 40. El 100% de ese delta se
        // leería como "vivo que bajó de precio" (una historia de optimización sobre un recurso que
        // sigue existiendo), escondiendo la baja real de 100 y el alta real de 60 que sí ocurrieron.
        var serieColapsadaPorNombre = new[] { 100m, 100m, 100m, 60m, 60m, 60m };
        var deltaColapsadoPorNombre = serieColapsadaPorNombre.Take(3).Average() - serieColapsadaPorNombre.Skip(3).Average();
        Assert.Equal(40m, deltaColapsadoPorNombre);
        // El neto coincide con la aritmética básica (100 de baja real menos 60 de alta real = 40)...
        Assert.Equal(40m, modelo.SinAtribuir.DejoDeFacturar.Total + modelo.SinAtribuir.Nuevo.Total);
        // ...pero el mecanismo al que se le atribuye NO: 40 entero en "cuesta menos" (por nombre) no
        // es lo mismo que 100 en "dejó de facturar" y −60 en "nuevo" (por terna). Es la distorsión
        // que el plan mide contra datos reales (6,8% contra 1,6% en la atribución).
        Assert.NotEqual(deltaColapsadoPorNombre, modelo.SinAtribuir.DejoDeFacturar.Total);
    }

    // --------------------------------------------------- Orden de los recursos ----

    [Fact]
    public void Cada_balde_ordena_sus_recursos_por_impacto_absoluto_descendente()
    {
        var filas = Meses("vm-chico", "sub-1", "rg-1", (1, 10m), (2, 10m), (3, 10m), (4, 5m), (5, 5m), (6, 5m))
            .Concat(Meses("vm-grande", "sub-1", "rg-1", (1, 500m), (2, 500m), (3, 500m), (4, 100m), (5, 100m), (6, 100m)))
            .ToList();

        var modelo = AtribucionCalculador.Calcular(filas, [], [], SinReservas, Contexto(1, 6));

        Assert.Equal("vm-grande", modelo!.SinAtribuir.VivoCuestaMenos.Recursos[0].ResourceName);
        Assert.Equal("vm-chico", modelo.SinAtribuir.VivoCuestaMenos.Recursos[1].ResourceName);
    }
}
