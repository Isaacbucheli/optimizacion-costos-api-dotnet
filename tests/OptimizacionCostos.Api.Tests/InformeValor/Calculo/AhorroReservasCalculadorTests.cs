using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Tarea 2 del plan de la entrega 2d (E2, E5): cruza <see cref="FotoReservas"/> (Tarea 1) contra
/// las filas de facturacion por terna. Pura: sin IO, sin reloj (ver SinRelojDelSistemaTests, que
/// escanea esta carpeta). La ventana antes/despues la marca la reserva (ExpiresOn menos Term), no
/// el mes en que bajo la factura — ese es justo el error que esta entrega corrige, asi que hay un
/// test dedicado a probarlo (<see cref="La_ventana_la_marca_la_reserva_no_el_mes_en_que_bajo_la_factura"/>).
/// </summary>
public sealed class AhorroReservasCalculadorTests
{
    private static ConsumidorReserva Consumidor(
        string instanceId = "/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1",
        string? nombre = "vm1", string? grupo = "rg1", string? suscripcion = "s1",
        double horas = 100) =>
        new(instanceId, nombre, grupo, suscripcion, "Standard_D2s_v5", horas, "2026-03-20", 30);

    private static ReservaActiva Reserva(
        string? id = "r1", string? nombre = "Reserva 1", string? producto = "Standard_D2s_v5",
        string? region = "eastus", int? cantidad = 1, string? term = "P1Y", string? expiresOn = "2027-03-15",
        bool expiring = false, string? utilUltimo = "85%", string? util7d = "80%",
        IReadOnlyList<ConsumidorReserva>? consumidores = null, int unidadesEstimadas = 0,
        bool consumidoresNoLeidos = false) =>
        new(id, nombre, producto, region, cantidad, term, "1 ano", expiresOn, 300, expiring,
            utilUltimo, util7d, consumidores ?? [], unidadesEstimadas, consumidoresNoLeidos);

    private static FotoReservas Foto(
        bool medido = true, string motivo = "ok", IReadOnlyList<object>? errores = null,
        int alertDays = 30, IReadOnlyList<ReservaActiva>? reservas = null) =>
        new(medido, motivo, errores ?? [], alertDays, new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            reservas ?? []);

    private static FacturacionRow Fila(
        string? subscriptionId = "s1", string? resourceGroup = "rg1", string? resourceName = "vm1",
        decimal pvp = 0, short year = 2026, byte month = 1) =>
        new(Hash: "h", Tenant: null, SubscriptionName: null, SubscriptionId: subscriptionId,
            ResourceGroup: resourceGroup, ResourceName: resourceName, CostCenter: null, Category: null,
            Subcategory: null, Service: null, Quantity: null, Unit: null, Rate: null,
            Pvp: pvp, Year: year, Month: month);

    /// <summary>Contexto amplio (2020-2030) para todos los tests que NO ejercitan la ventana fija de
    /// E9: alcanza para que ConsumoCalculador.EnRango nunca excluya ninguna fila de estos fixtures,
    /// sin tener que elegir un contexto propio para cada año/mes que ya usan.</summary>
    private static readonly ContextoInformeValor ContextoAmplio =
        new(new DateOnly(2020, 1, 1), new DateOnly(2030, 12, 31), new DateOnly(2026, 8, 1), null);

    // ── El eje no medido se propaga: no hay ahorro "en cero" cuando no se pudo leer la reserva ──

    [Fact]
    public void Si_la_foto_no_esta_medida_el_modelo_tampoco_lo_esta()
    {
        var foto = Foto(medido: false, motivo: "sin credenciales", errores: [new { error = "x" }]);

        var modelo = AhorroReservasCalculador.Calcular(foto, [], [], ContextoAmplio);

        Assert.False(modelo.Medido);
        Assert.Equal("sin credenciales", modelo.Motivo);
        Assert.Empty(modelo.Confirmados);
        Assert.Equal(0m, modelo.AhorroConfirmado);
    }

    // ── Caso feliz: antes vs despues, sobre las mismas horas que reporta la reserva ──

    [Fact]
    public void Calcula_el_ahorro_confirmado_como_diferencia_de_tarifa_por_hora_sobre_las_horas_usadas()
    {
        // inicio de la reserva = 2027-03-15 (ExpiresOn) - 1 año (Term) = 2026-03-xx -> mes 2026-03.
        var reserva = Reserva(consumidores: [Consumidor(horas: 100)]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = new[]
        {
            Fila(pvp: 730m, year: 2026, month: 1),  // antes: 1 $/hora (730 / 730h)
            Fila(pvp: 730m, year: 2026, month: 2),  // antes: 1 $/hora
            Fila(pvp: 73m, year: 2026, month: 3),   // despues: 0.1 $/hora (mes del inicio, entra completo)
            Fila(pvp: 73m, year: 2026, month: 4),   // despues: 0.1 $/hora
        };

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoAmplio);

        var item = Assert.Single(modelo.Confirmados);
        Assert.Equal(1m, item.TarifaAntesPorHora);
        Assert.Equal(0.1m, item.TarifaDespuesPorHora);
        Assert.Equal(90m, item.Ahorro); // (1 - 0.1) * 100 horas
        Assert.Equal(90m, modelo.AhorroConfirmado);
    }

    /// <summary>La correccion que origino esta entrega: la ventana la marca la reserva
    /// (ExpiresOn - Term), nunca el mes en que la factura bajo. Aca el precio baja un mes ANTES
    /// del inicio real de la reserva (podria ser un descuento no relacionado); si el calculo
    /// usara "el mes en que bajo la factura" como frontera, el mes 2026-02 caeria del lado
    /// "despues" y la tarifa de antes saldria distinta.</summary>
    [Fact]
    public void La_ventana_la_marca_la_reserva_no_el_mes_en_que_bajo_la_factura()
    {
        var reserva = Reserva(expiresOn: "2027-03-15", term: "P1Y", consumidores: [Consumidor(horas: 10)]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = new[]
        {
            Fila(pvp: 730m, year: 2026, month: 1),  // antes
            Fila(pvp: 73m, year: 2026, month: 2),   // baja ANTES del inicio real (2026-03): sigue siendo "antes"
            Fila(pvp: 73m, year: 2026, month: 3),   // despues (mes del inicio)
        };

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoAmplio);

        var item = Assert.Single(modelo.Confirmados);
        // Si la frontera fuera "donde bajo la factura", antes seria solo enero (1 $/h). Con la
        // regla correcta, antes es el promedio ponderado de enero Y febrero: (730+73)/(2*730).
        Assert.Equal((730m + 73m) / (2m * 730m), item.TarifaAntesPorHora);
    }

    [Fact]
    public void Sin_facturacion_anterior_al_inicio_no_se_calcula_el_ahorro()
    {
        var reserva = Reserva(consumidores: [Consumidor()]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = new[] { Fila(pvp: 10m, year: 2026, month: 3) }; // solo "despues"

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoAmplio);

        var item = Assert.Single(modelo.Confirmados);
        Assert.Null(item.Ahorro);
        Assert.NotNull(item.MotivoSinCalcular);
    }

    [Fact]
    public void Sin_facturacion_posterior_al_inicio_no_se_calcula_el_ahorro()
    {
        var reserva = Reserva(consumidores: [Consumidor()]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = new[] { Fila(pvp: 730m, year: 2025, month: 12) }; // solo "antes"

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoAmplio);

        var item = Assert.Single(modelo.Confirmados);
        Assert.Null(item.Ahorro);
        Assert.NotNull(item.MotivoSinCalcular);
    }

    [Fact]
    public void Un_recurso_confirmado_que_no_aparece_en_la_facturacion_no_se_calcula()
    {
        var reserva = Reserva(consumidores: [Consumidor(nombre: "vm-fantasma", grupo: "rg9", suscripcion: "s9")]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = new[] { Fila(subscriptionId: "s1", resourceGroup: "rg1", resourceName: "vm1", pvp: 100m) };

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoAmplio);

        var item = Assert.Single(modelo.Confirmados);
        Assert.Null(item.Ahorro);
    }

    [Fact]
    public void Un_termino_no_reconocido_no_permite_derivar_el_inicio_ni_calcular()
    {
        var reserva = Reserva(term: "desconocido", consumidores: [Consumidor()]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = new[] { Fila(pvp: 100m, year: 2026, month: 1) };

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoAmplio);

        var item = Assert.Single(modelo.Confirmados);
        Assert.Null(item.Ahorro);
        Assert.NotNull(item.MotivoSinCalcular);
    }

    // ── La terna hace el cruce sin distinguir mayusculas/minusculas (mismo criterio que
    // RiCoverageService.Norm para su propio cruce por instanceId) ──

    [Fact]
    public void El_cruce_por_terna_no_distingue_mayusculas()
    {
        var reserva = Reserva(consumidores: [Consumidor(nombre: "VM1", grupo: "RG1", suscripcion: "S1", horas: 10)]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = new[]
        {
            Fila(subscriptionId: "s1", resourceGroup: "rg1", resourceName: "vm1", pvp: 730m, year: 2026, month: 1),
            Fila(subscriptionId: "s1", resourceGroup: "rg1", resourceName: "vm1", pvp: 0m, year: 2026, month: 3),
        };

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoAmplio);

        var item = Assert.Single(modelo.Confirmados);
        Assert.NotNull(item.Ahorro);
    }

    // ── Utilizacion y vencimiento viajan junto al ahorro (E2, E5): el consultor tiene que poder
    // ver "ahorro alto, utilizacion baja" como una conclusion distinta ──

    [Fact]
    public void La_utilizacion_y_la_marca_de_proxima_a_vencer_viajan_con_el_ahorro()
    {
        var reserva = Reserva(expiring: true, utilUltimo: "12%", util7d: "15%", consumidores: [Consumidor()]);
        var foto = Foto(reservas: [reserva]);

        var modelo = AhorroReservasCalculador.Calcular(foto, [], [], ContextoAmplio);

        var item = Assert.Single(modelo.Confirmados);
        Assert.True(item.Expiring);
        Assert.Equal("12%", item.UtilizationLast);
        Assert.Equal("15%", item.Utilization7d);
    }

    // ── Discrepancia: la app confirma cobertura pero la facturacion no muestra una baja ──

    [Fact]
    public void Cobertura_confirmada_sin_baja_de_tarifa_es_una_discrepancia()
    {
        var reserva = Reserva(consumidores: [Consumidor(horas: 10)]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = new[]
        {
            Fila(pvp: 730m, year: 2026, month: 1), // antes: 1 $/h
            Fila(pvp: 730m, year: 2026, month: 3), // despues: 1 $/h -> sin baja
        };

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoAmplio);

        Assert.Single(modelo.Discrepancias);
        Assert.True(modelo.Confirmados[0].Ahorro <= 0m);
    }

    [Fact]
    public void Una_baja_real_de_tarifa_no_genera_discrepancia()
    {
        var reserva = Reserva(consumidores: [Consumidor(horas: 10)]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = new[]
        {
            Fila(pvp: 730m, year: 2026, month: 1),
            Fila(pvp: 0m, year: 2026, month: 3),
        };

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoAmplio);

        Assert.Empty(modelo.Discrepancias);
    }

    // ── Los estimados se publican aparte, sin costo asociado a un recurso puntual (E2) ──

    [Fact]
    public void Las_unidades_estimadas_se_publican_aparte_sin_costo()
    {
        var reserva = Reserva(cantidad: 3, unidadesEstimadas: 2, consumidores: [Consumidor()]);
        var foto = Foto(reservas: [reserva]);

        var modelo = AhorroReservasCalculador.Calcular(foto, [], [], ContextoAmplio);

        var estimado = Assert.Single(modelo.Estimados);
        Assert.Equal(2, estimado.UnidadesEstimadas);
        Assert.Equal("r1", estimado.ReservationId);
    }

    [Fact]
    public void Sin_unidades_estimadas_no_se_publica_ninguna_fila_de_estimado()
    {
        var reserva = Reserva(unidadesEstimadas: 0, consumidores: [Consumidor()]);
        var foto = Foto(reservas: [reserva]);

        var modelo = AhorroReservasCalculador.Calcular(foto, [], [], ContextoAmplio);

        Assert.Empty(modelo.Estimados);
    }

    // ── La marca de "no se pudieron leer los consumidores" llega al modelo publicado ──

    /// <summary>
    /// El caso que este test protege: el app registration del cliente lista reservas
    /// (Microsoft.Capacity) pero no puede leer consumidores (Microsoft.Consumption). La foto sale
    /// MEDIDA —la lista se leyo bien, y esa falla nunca aparece en <c>errores</c>— con cada reserva
    /// marcada. Si la marca muere en la foto, lo que se publica es indistinguible del caso legitimo:
    /// cero confirmados, cero ahorro, la cantidad entera en estimado. Uno significa "el ahorro esta
    /// subestimado y nadie lo sabe" y el otro es normal.
    /// </summary>
    [Fact]
    public void Una_reserva_cuyos_consumidores_no_se_pudieron_leer_no_se_publica_igual_que_una_sin_consumidores()
    {
        var conFalla = AhorroReservasCalculador.Calcular(
            Foto(reservas: [Reserva(cantidad: 2, unidadesEstimadas: 2, consumidoresNoLeidos: true)]),
            [], [], ContextoAmplio);
        var sinConsumidores = AhorroReservasCalculador.Calcular(
            Foto(reservas: [Reserva(cantidad: 2, unidadesEstimadas: 2, consumidoresNoLeidos: false)]),
            [], [], ContextoAmplio);

        // Todo lo demas es identico entre los dos: es exactamente por eso que hace falta la marca.
        Assert.Equal(sinConsumidores.AhorroConfirmado, conFalla.AhorroConfirmado);
        Assert.Equal(sinConsumidores.Estimados[0].UnidadesEstimadas, conFalla.Estimados[0].UnidadesEstimadas);

        Assert.Equal(1, conFalla.ReservasConConsumidoresNoLeidos);
        Assert.True(conFalla.Estimados[0].ConsumidoresNoLeidos);
        Assert.Equal(0, sinConsumidores.ReservasConConsumidoresNoLeidos);
        Assert.False(sinConsumidores.Estimados[0].ConsumidoresNoLeidos);
    }

    /// <summary>Una reserva que Azure devuelve sin <c>quantity</c> deja
    /// <see cref="ReservaActiva.UnidadesEstimadas"/> en cero: la fila de estimado se publica igual
    /// cuando la lectura fallo, porque es el unico lugar del modelo donde esa reserva puntual
    /// aparece. Sin esto la falla desaparece del bloque, salvo por el conteo.</summary>
    [Fact]
    public void Una_reserva_sin_cantidad_cuya_lectura_fallo_igual_se_publica_como_estimada()
    {
        var reserva = Reserva(cantidad: null, unidadesEstimadas: 0, consumidoresNoLeidos: true);

        var modelo = AhorroReservasCalculador.Calcular(Foto(reservas: [reserva]), [], [], ContextoAmplio);

        var estimado = Assert.Single(modelo.Estimados);
        Assert.Equal(0, estimado.UnidadesEstimadas);
        Assert.True(estimado.ConsumidoresNoLeidos);
        Assert.Equal(1, modelo.ReservasConConsumidoresNoLeidos);
    }

    // ── El total confirmado suma los ahorros calculables, ignora los que no se pudieron calcular ──

    [Fact]
    public void El_total_confirmado_suma_solo_los_ahorros_calculables()
    {
        var reservaConAhorro = Reserva(id: "r1", consumidores: [Consumidor(instanceId: "/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1", nombre: "vm1", grupo: "rg1", suscripcion: "s1", horas: 10)]);
        var reservaSinDatos = Reserva(id: "r2", consumidores:
        [
            Consumidor(instanceId: "/subscriptions/s2/resourceGroups/rg2/providers/Microsoft.Compute/virtualMachines/vm2", nombre: "vm2", grupo: "rg2", suscripcion: "s2", horas: 10),
        ]);
        var foto = Foto(reservas: [reservaConAhorro, reservaSinDatos]);
        var facturacion = new[]
        {
            Fila(subscriptionId: "s1", resourceGroup: "rg1", resourceName: "vm1", pvp: 730m, year: 2026, month: 1),
            Fila(subscriptionId: "s1", resourceGroup: "rg1", resourceName: "vm1", pvp: 0m, year: 2026, month: 3),
            // vm2 no aparece en absoluto en la facturacion: su ahorro queda null y no suma.
        };

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoAmplio);

        Assert.Equal(2, modelo.Confirmados.Count);
        Assert.Equal(10m, modelo.AhorroConfirmado); // (1 - 0) * 10, solo la de vm1
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // E9 (entrega 2d, tarea 5, la costura con los baldes 2 y 3): la fecha de la reserva decide SI
    // explica algo dentro del período del informe; si sí, el aporte se mide sobre la MISMA ventana
    // fija que AtribucionCalculador (base = todos los meses no parciales menos los últimos tres,
    // cierre = esos últimos tres), nunca sobre la tarifa por hora desde el propio inicio de la
    // reserva (esa sigue existiendo en Ahorro/TarifaAntesPorHora/TarifaDespuesPorHora, sin cambios).
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Ventana [ene, jun] 2026 (el mínimo de seis meses): base = ene-mar, cierre = abr-jun,
    /// igual partición que <c>AtribucionCalculador.Calcular</c> usa internamente.</summary>
    private static ContextoInformeValor ContextoVentana(int mesInicio = 1, int mesFin = 6) => new(
        new DateOnly(2026, mesInicio, 1),
        new DateOnly(2026, mesFin, DateTime.DaysInMonth(2026, mesFin)),
        Corte: new DateOnly(2026, mesFin, DateTime.DaysInMonth(2026, mesFin)),
        MesesParcialesForzados: null);

    /// <summary>Seis meses de facturación de un solo recurso: <paramref name="antes"/> en ene-mar,
    /// <paramref name="despues"/> en abr-jun — la ventana base/cierre completa, sin necesitar un
    /// recurso ancla aparte (a diferencia de los tests de dedup/redondeo más abajo, donde el recurso
    /// bajo prueba solo tiene UNA fila y hace falta un ancla para que la ventana de seis meses
    /// exista de todos modos).</summary>
    private static IReadOnlyList<FacturacionRow> SeisMeses(
        string subscriptionId, string resourceGroup, string resourceName, decimal antes, decimal despues) =>
    [
        Fila(subscriptionId, resourceGroup, resourceName, pvp: antes, year: 2026, month: 1),
        Fila(subscriptionId, resourceGroup, resourceName, pvp: antes, year: 2026, month: 2),
        Fila(subscriptionId, resourceGroup, resourceName, pvp: antes, year: 2026, month: 3),
        Fila(subscriptionId, resourceGroup, resourceName, pvp: despues, year: 2026, month: 4),
        Fila(subscriptionId, resourceGroup, resourceName, pvp: despues, year: 2026, month: 5),
        Fila(subscriptionId, resourceGroup, resourceName, pvp: despues, year: 2026, month: 6),
    ];

    /// <summary>El caso que nombra E9: una reserva que arrancó antes de que empiece la ventana ya
    /// tenía al recurso cubierto durante TODO el período, así que cualquier variación que se vea
    /// adentro (acá, una baja de $10/mes ajena a la reserva) no la explica ella — ni aunque el
    /// cálculo "ingenuo" sobre la ventana daría un número que no es cero.</summary>
    [Fact]
    public void Una_reserva_que_arranca_antes_de_la_ventana_no_explica_el_periodo_aunque_la_tarifa_cambie()
    {
        // inicio = 2026-10-05 (ExpiresOn) - 1 año (Term) = 2025-10-05 -> "2025-10", anterior a "2026-01".
        var reserva = Reserva(expiresOn: "2026-10-05", term: "P1Y", consumidores: [Consumidor(horas: 10)]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = SeisMeses("s1", "rg1", "vm1", antes: 60m, despues: 50m);

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoVentana());

        var item = Assert.Single(modelo.Confirmados);
        Assert.False(item.ExplicaElPeriodo);
        Assert.Null(item.AporteAlPeriodo);
        Assert.Empty(modelo.RecursosQueExplicanElPeriodo);
        Assert.Equal(0m, modelo.AporteAlPeriodo);
    }

    [Fact]
    public void Una_reserva_que_arranca_dentro_de_la_ventana_explica_el_periodo_y_el_aporte_se_mide_sobre_esa_ventana()
    {
        // inicio = 2027-04-10 - 1 año = 2026-04-10 -> "2026-04", dentro de la ventana de cierre.
        var reserva = Reserva(expiresOn: "2027-04-10", term: "P1Y", consumidores: [Consumidor(horas: 100)]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = SeisMeses("s1", "rg1", "vm1", antes: 200m, despues: 80m);

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoVentana());

        var item = Assert.Single(modelo.Confirmados);
        Assert.True(item.ExplicaElPeriodo);
        Assert.Equal(120m, item.AporteAlPeriodo); // promedio base (200) - promedio cierre (80)
        Assert.Equal(120m, modelo.AporteAlPeriodo);
        Assert.Equal("s1|rg1|vm1", Assert.Single(modelo.RecursosQueExplicanElPeriodo));
        // Contraste explícito: el ahorro "desde el inicio" (tarifa por hora, otra ventana) es un
        // número distinto — las dos cifras no son la misma pregunta, ver el comentario de clase.
        Assert.NotEqual(120m, item.Ahorro);
    }

    /// <summary>Frontera inferior: si la reserva arranca justo en el PRIMER mes de la ventana, toda
    /// la ventana (base y cierre) ya ve al recurso cubierto — no hay "antes" que ver adentro.</summary>
    [Fact]
    public void Una_reserva_que_arranca_justo_en_el_primer_mes_de_la_ventana_no_explica_nada_adentro()
    {
        // inicio = 2027-01-15 - 1 año = 2026-01-15 -> "2026-01", el primer mes de la ventana base.
        var reserva = Reserva(expiresOn: "2027-01-15", term: "P1Y", consumidores: [Consumidor()]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = SeisMeses("s1", "rg1", "vm1", antes: 100m, despues: 100m);

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoVentana());

        Assert.False(Assert.Single(modelo.Confirmados).ExplicaElPeriodo);
    }

    /// <summary>Frontera superior, el caso simétrico: si arranca en el ÚLTIMO mes de la ventana de
    /// cierre, todavía queda un "antes" completo (ene-may) que ver adentro — sí explica.</summary>
    [Fact]
    public void Una_reserva_que_arranca_en_el_ultimo_mes_de_la_ventana_si_explica_el_periodo()
    {
        // inicio = 2027-06-20 - 1 año = 2026-06-20 -> "2026-06", el último mes de la ventana.
        var reserva = Reserva(expiresOn: "2027-06-20", term: "P1Y", consumidores: [Consumidor()]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = SeisMeses("s1", "rg1", "vm1", antes: 100m, despues: 100m);

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoVentana());

        Assert.True(Assert.Single(modelo.Confirmados).ExplicaElPeriodo);
    }

    /// <summary>El caso simétrico al que nombra E9: una reserva que arranca DESPUÉS del fin de la
    /// ventana (ej. el informe es de enero-junio, pero la reserva se compró en agosto y la foto se
    /// tomó más tarde) tampoco existía todavía durante el período: no puede explicar nada de lo que
    /// pasó ahí.</summary>
    [Fact]
    public void Una_reserva_que_arranca_despues_del_fin_de_la_ventana_no_explica_nada()
    {
        // inicio = 2028-08-01 - 1 año = 2027-08-01 -> "2027-08", posterior a "2026-06".
        var reserva = Reserva(expiresOn: "2028-08-01", term: "P1Y", consumidores: [Consumidor()]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = SeisMeses("s1", "rg1", "vm1", antes: 100m, despues: 100m);

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoVentana());

        Assert.False(Assert.Single(modelo.Confirmados).ExplicaElPeriodo);
    }

    [Fact]
    public void Sin_seis_meses_no_parciales_en_el_rango_ninguna_reserva_explica_el_periodo()
    {
        var reserva = Reserva(expiresOn: "2027-02-10", term: "P1Y", consumidores: [Consumidor()]); // inicio 2026-02
        var foto = Foto(reservas: [reserva]);
        var facturacion = new[]
        {
            Fila("s1", "rg1", "vm1", pvp: 100m, year: 2026, month: 1),
            Fila("s1", "rg1", "vm1", pvp: 100m, year: 2026, month: 2),
            Fila("s1", "rg1", "vm1", pvp: 100m, year: 2026, month: 3), // solo tres meses: menos del mínimo de seis
        };

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoVentana(1, 3));

        Assert.False(Assert.Single(modelo.Confirmados).ExplicaElPeriodo);
        Assert.Empty(modelo.RecursosQueExplicanElPeriodo);
        Assert.Equal(0m, modelo.AporteAlPeriodo);
    }

    /// <summary>El mismo recurso puede aparecer como consumidor confirmado de dos reservas distintas
    /// (ej. una migración de una RI más chica a una más grande): si las dos son elegibles, el aporte
    /// —que no depende de la reserva, solo del propio recurso y de la ventana— se cuenta UNA sola
    /// vez, no una vez por reserva.</summary>
    [Fact]
    public void El_mismo_recurso_confirmado_bajo_dos_reservas_elegibles_no_se_cuenta_dos_veces()
    {
        var reservaA = Reserva(id: "rA", expiresOn: "2027-04-10", term: "P1Y", // inicio 2026-04, elegible
            consumidores: [Consumidor(horas: 10)]);
        var reservaB = Reserva(id: "rB", expiresOn: "2027-05-10", term: "P1Y", // inicio 2026-05, tambien elegible
            consumidores: [Consumidor(horas: 20)]);
        var foto = Foto(reservas: [reservaA, reservaB]);
        var facturacion = SeisMeses("s1", "rg1", "vm1", antes: 200m, despues: 80m);

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoVentana());

        Assert.Equal(2, modelo.Confirmados.Count); // una fila por par reserva-consumidor, sin colapsar
        Assert.All(modelo.Confirmados, c => Assert.True(c.ExplicaElPeriodo));
        Assert.All(modelo.Confirmados, c => Assert.Equal(120m, c.AporteAlPeriodo));
        Assert.Equal(120m, modelo.AporteAlPeriodo); // no 240: el mismo recurso, contado una vez
        Assert.Equal("s1|rg1|vm1", Assert.Single(modelo.RecursosQueExplicanElPeriodo));
    }

    /// <summary>
    /// E1, mismo criterio que fija el test análogo de <c>AtribucionCalculadorTests</c>: el total se
    /// redondea UNA vez desde la suma SIN redondear de los aportes crudos, nunca desde la suma de
    /// los <see cref="AhorroPorRecurso.AporteAlPeriodo"/> ya redondeados de cada recurso. vmA aporta
    /// un crudo de 1.005 (redondea a 1.01, +0.005); vmB aporta 2.005 (redondea a 2.01, +0.005).
    /// Sumados YA redondeados: 1.01+2.01=3.02. Sumados crudos y redondeados una sola vez al final:
    /// 1.005+2.005=3.010, que redondea a 3.01. 3.02 ≠ 3.01.
    /// </summary>
    [Fact]
    public void El_aporte_al_periodo_se_redondea_una_sola_vez_desde_la_suma_cruda_no_desde_los_ya_redondeados()
    {
        var reserva = Reserva(expiresOn: "2027-04-10", term: "P1Y", consumidores: // inicio 2026-04, elegible
        [
            Consumidor(instanceId: "iA", nombre: "vmA", grupo: "rgA", suscripcion: "s1", horas: 1),
            Consumidor(instanceId: "iB", nombre: "vmB", grupo: "rgB", suscripcion: "s1", horas: 1),
        ]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = new[]
        {
            // vm-ancla: solo para que la ventana de seis meses exista (vmA/vmB no facturan en abr-jun).
            Fila("s1", "rg-ancla", "vm-ancla", pvp: 1m, year: 2026, month: 1),
            Fila("s1", "rg-ancla", "vm-ancla", pvp: 1m, year: 2026, month: 2),
            Fila("s1", "rg-ancla", "vm-ancla", pvp: 1m, year: 2026, month: 3),
            Fila("s1", "rg-ancla", "vm-ancla", pvp: 1m, year: 2026, month: 4),
            Fila("s1", "rg-ancla", "vm-ancla", pvp: 1m, year: 2026, month: 5),
            Fila("s1", "rg-ancla", "vm-ancla", pvp: 1m, year: 2026, month: 6),
            // vmA aporta crudo 1.005 (3.015/3 de base, 0 de cierre); vmB aporta crudo 2.005 (6.015/3).
            Fila("s1", "rgA", "vmA", pvp: 3.015m, year: 2026, month: 1),
            Fila("s1", "rgB", "vmB", pvp: 6.015m, year: 2026, month: 1),
        };

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion, [], ContextoVentana());

        var vmA = Assert.Single(modelo.Confirmados, c => c.ResourceName == "vmA");
        var vmB = Assert.Single(modelo.Confirmados, c => c.ResourceName == "vmB");
        Assert.Equal(1.01m, vmA.AporteAlPeriodo);
        Assert.Equal(2.01m, vmB.AporteAlPeriodo);
        Assert.Equal(3.01m, modelo.AporteAlPeriodo); // 1.005+2.005=3.010 -> 3.01, NO 1.01+2.01=3.02
    }
}
