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
        IReadOnlyList<ConsumidorReserva>? consumidores = null, int unidadesEstimadas = 0) =>
        new(id, nombre, producto, region, cantidad, term, "1 ano", expiresOn, 300, expiring,
            utilUltimo, util7d, consumidores ?? [], unidadesEstimadas);

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

    // ── El eje no medido se propaga: no hay ahorro "en cero" cuando no se pudo leer la reserva ──

    [Fact]
    public void Si_la_foto_no_esta_medida_el_modelo_tampoco_lo_esta()
    {
        var foto = Foto(medido: false, motivo: "sin credenciales", errores: [new { error = "x" }]);

        var modelo = AhorroReservasCalculador.Calcular(foto, []);

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

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion);

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

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion);

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

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion);

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

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion);

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

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion);

        var item = Assert.Single(modelo.Confirmados);
        Assert.Null(item.Ahorro);
    }

    [Fact]
    public void Un_termino_no_reconocido_no_permite_derivar_el_inicio_ni_calcular()
    {
        var reserva = Reserva(term: "desconocido", consumidores: [Consumidor()]);
        var foto = Foto(reservas: [reserva]);
        var facturacion = new[] { Fila(pvp: 100m, year: 2026, month: 1) };

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion);

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

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion);

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

        var modelo = AhorroReservasCalculador.Calcular(foto, []);

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

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion);

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

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion);

        Assert.Empty(modelo.Discrepancias);
    }

    // ── Los estimados se publican aparte, sin costo asociado a un recurso puntual (E2) ──

    [Fact]
    public void Las_unidades_estimadas_se_publican_aparte_sin_costo()
    {
        var reserva = Reserva(cantidad: 3, unidadesEstimadas: 2, consumidores: [Consumidor()]);
        var foto = Foto(reservas: [reserva]);

        var modelo = AhorroReservasCalculador.Calcular(foto, []);

        var estimado = Assert.Single(modelo.Estimados);
        Assert.Equal(2, estimado.UnidadesEstimadas);
        Assert.Equal("r1", estimado.ReservationId);
    }

    [Fact]
    public void Sin_unidades_estimadas_no_se_publica_ninguna_fila_de_estimado()
    {
        var reserva = Reserva(unidadesEstimadas: 0, consumidores: [Consumidor()]);
        var foto = Foto(reservas: [reserva]);

        var modelo = AhorroReservasCalculador.Calcular(foto, []);

        Assert.Empty(modelo.Estimados);
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

        var modelo = AhorroReservasCalculador.Calcular(foto, facturacion);

        Assert.Equal(2, modelo.Confirmados.Count);
        Assert.Equal(10m, modelo.AhorroConfirmado); // (1 - 0) * 10, solo la de vm1
    }
}
