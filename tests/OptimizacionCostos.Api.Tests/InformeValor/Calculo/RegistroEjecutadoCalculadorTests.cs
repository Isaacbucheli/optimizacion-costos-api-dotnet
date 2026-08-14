using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Tarea 4 del plan de la entrega 6: las filas del registro de lo ejecutado, una por acción
/// (barrido/matriz/reserva) con su monto rotulado por fuente. Pura y sin reloj (ver
/// <c>SinRelojDelSistemaTests</c>, que escanea <c>Calculo/</c> completo).
/// </summary>
public sealed class RegistroEjecutadoCalculadorTests
{
    private static int _n;

    // ── Builders de insumos ──

    private static FacturacionRow Fila(
        string? subscriptionId, string? resourceGroup, string? resourceName, decimal pvp, short year, byte month,
        string? category = "Cómputo", string? subscriptionName = null) =>
        new(Hash: $"h{++_n}", Tenant: null, SubscriptionName: subscriptionName, SubscriptionId: subscriptionId,
            ResourceGroup: resourceGroup, ResourceName: resourceName, CostCenter: null, Category: category,
            Subcategory: null, Service: null, Quantity: null, Unit: null, Rate: null,
            Pvp: pvp, Year: year, Month: month);

    private static BarridoResueltoFila BarridoFila(
        string checkId, string subscriptionId, string resourceGroup, string? resourceName, DateTime resueltoEn,
        decimal? estimatedMonthlySavings = null, string? resolvedByKind = "manual") =>
        new(CheckId: checkId, SubscriptionId: subscriptionId,
            AzureResourceId: $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{resourceName}",
            ResourceName: resourceName, ResourceType: "microsoft.compute/virtualmachines",
            EstimatedMonthlySavings: estimatedMonthlySavings, Currency: "USD", ResueltoEn: resueltoEn,
            ResueltoPor: "consultor@bit.com", ResolvedByKind: resolvedByKind, Notas: null);

    private static HallazgoResueltoFila MatrizFila(
        string subscriptionId, string resourceGroup, string resourceName, DateOnly? resolvedAt,
        string hallazgo = "Habilitar diagnósticos", string? matrixCode = "5.1", int pillarNumber = 5) =>
        new(SubscriptionId: subscriptionId, SubscriptionName: "Sub 1", ResourceGroup: resourceGroup,
            ResourceName: resourceName, ResolvedAt: resolvedAt, MatrixCode: matrixCode, Hallazgo: hallazgo,
            PillarNumber: pillarNumber);

    private static ConsumidorReserva Consumidor(
        string resourceName, string? resourceGroup = "rg1", string? subscriptionId = "s1", string? sku = "Standard_B16ms") =>
        new(InstanceId: $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{resourceName}",
            ResourceName: resourceName, ResourceGroup: resourceGroup, SubscriptionId: subscriptionId,
            SkuName: sku, UsedHours: 720, LastSeen: "2026-02-28", DaysSeen: 28);

    private static ReservaActiva Reserva(
        string? id, string? nombre, string? producto, string? term, string? expiresOn,
        IReadOnlyList<ConsumidorReserva> consumidores) =>
        new(ReservationId: id, Nombre: nombre, Producto: producto, Region: "eastus", Cantidad: consumidores.Count,
            Term: term, TermLabel: term, ExpiresOn: expiresOn, DaysRemaining: 300, Expiring: false,
            UtilizationLast: "90%", Utilization7d: "88%", Consumidores: consumidores,
            UnidadesEstimadas: 0, ConsumidoresNoLeidos: false);

    private static ReservaVmFila VmFila(string reservationId, string vm, decimal? demanda, decimal reservaMes, decimal? ahorro) =>
        new(ReservationId: reservationId, Vm: vm, Sku: "Standard_B16ms", PorDemandaMes: demanda,
            ReservaMes: reservaMes, AhorroMes: ahorro, Compartida: false, Vence: "2027-03-15", PorVencer: false, Nota: null);

    private static FotoReservas Foto(bool medido = true, string motivo = "ok", IReadOnlyList<ReservaActiva>? reservas = null) =>
        new(Medido: medido, Motivo: motivo, Errores: [], AlertDays: 30,
            CapturadaEn: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), Reservas: reservas ?? []);

    private static ReservasFacturadasModelo ModeloReservas(
        bool medido = true, string? motivo = null, IReadOnlyList<ReservaVmFila>? filas = null) =>
        new(Medido: medido, Motivo: motivo, Filas: filas ?? [], TotalDemanda: 0m, TotalReserva: 0m, TotalAhorro: 0m,
            AhorroAnualizado: 0m, SinLineaEnEvolucion: [], ConsumidoresNoLeidos: 0);

    /// <summary>Rango amplio: ninguno de los fixtures necesita ejercitar D0 de por sí.</summary>
    private static readonly ContextoInformeValor ContextoAmplio =
        new(new DateOnly(2020, 1, 1), new DateOnly(2030, 12, 31), new DateOnly(2026, 6, 1), null);

    private static (IReadOnlyList<AccionEjecutada> Filas, RegistroEjes Ejes) Calcular(
        RegistroBarrido? barrido = null,
        IReadOnlyList<HallazgoResueltoFila>? hallazgosMatriz = null,
        ReservasFacturadasModelo? reservasFacturadas = null,
        FotoReservas? fotoReservas = null,
        IReadOnlyList<FacturacionRow>? facturacion = null,
        ContextoInformeValor? contexto = null) =>
        RegistroEjecutadoCalculador.Calcular(
            barrido ?? RegistroBarrido.SinBarrido(),
            hallazgosMatriz ?? [],
            reservasFacturadas ?? ModeloReservas(medido: false, motivo: "sin reservas"),
            fotoReservas ?? Foto(medido: false, motivo: "sin reservas"),
            facturacion ?? [],
            contexto ?? ContextoAmplio);

    // ── Regla 1: barrido → fila ──

    [Fact]
    public void Barrido_resuelto_manual_produce_fila_declarada_con_check_mapeado_y_terna_extraida_del_id_arm()
    {
        var barrido = new RegistroBarrido(true, null,
            [BarridoFila("orphaned_disks", "s1", "rg1", "disk1", new DateTime(2026, 4, 10), resolvedByKind: "manual")]);

        var (filas, _) = Calcular(barrido: barrido);

        var fila = Assert.Single(filas);
        Assert.Equal("barrido", fila.Fuente);
        Assert.Equal("Discos administrados no conectados", fila.Oportunidad); // CheckDefinition.Name de orphaned_disks
        Assert.Equal("Discos / Réplicas", fila.Categoria); // CategoriaEjecutado.PorCheck
        Assert.Equal("s1", fila.SubscriptionId);
        Assert.Equal("rg1", fila.ResourceGroup); // extraido del AzureResourceId
        Assert.Equal("disk1", fila.ResourceName);
        Assert.Equal("2026-04", fila.MesEjecucion);
        Assert.Equal("declarada", fila.Autoria);
    }

    [Fact]
    public void Barrido_con_checkId_desconocido_usa_el_id_crudo_como_oportunidad_y_resolved_by_kind_null_es_indeterminada()
    {
        var barrido = new RegistroBarrido(true, null,
            [BarridoFila("check_futuro_no_registrado", "s1", "rg1", "vm1", new DateTime(2026, 4, 10), resolvedByKind: null)]);

        var (filas, ejes) = Calcular(barrido: barrido);

        var fila = Assert.Single(filas);
        Assert.Equal("check_futuro_no_registrado", fila.Oportunidad);
        Assert.Equal("indeterminada", fila.Autoria);
        Assert.Equal(1, ejes.Indeterminadas);
    }

    // ── Regla 2: matriz → fila ──

    [Fact]
    public void Matriz_resuelta_con_fecha_produce_fila_declarada_con_el_texto_del_hallazgo()
    {
        var hallazgos = new[] { MatrizFila("s1", "rg1", "vm1", new DateOnly(2026, 5, 3), hallazgo: "Habilitar backup") };

        var (filas, _) = Calcular(hallazgosMatriz: hallazgos);

        var fila = Assert.Single(filas);
        Assert.Equal("matriz", fila.Fuente);
        Assert.Equal("Habilitar backup", fila.Oportunidad);
        Assert.Equal("2026-05", fila.MesEjecucion);
        Assert.Equal("declarada", fila.Autoria);
    }

    [Fact]
    public void Matriz_sin_fecha_de_resolucion_se_descarta_no_se_puede_ubicar_en_el_tiempo()
    {
        var hallazgos = new[] { MatrizFila("s1", "rg1", "vm1", resolvedAt: null) };

        var (filas, _) = Calcular(hallazgosMatriz: hallazgos);

        Assert.Empty(filas);
    }

    [Fact]
    public void Matriz_resuelta_antes_del_inicio_del_periodo_igual_produce_fila_el_rango_no_la_descarta()
    {
        // A diferencia del barrido/reserva, la matriz NO se filtra por PeriodStart/PeriodEnd: una
        // acción anterior al rango del informe igual aporta su tasa al acumulado (Tarea 5).
        var contexto = new ContextoInformeValor(new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31), new DateOnly(2026, 6, 1), null);
        var hallazgos = new[] { MatrizFila("s1", "rg1", "vm1", new DateOnly(2026, 3, 15)) };
        // Sin facturación dentro del rango (jun-ago) anterior a marzo: sin "antes" que promediar,
        // la fila existe igual pero sin monto (regla 4/6 estándar).

        var (filas, _) = Calcular(hallazgosMatriz: hallazgos, contexto: contexto);

        var fila = Assert.Single(filas);
        Assert.Equal("matriz", fila.Fuente);
        Assert.Equal("2026-03", fila.MesEjecucion);
        Assert.Null(fila.MontoMensual);
        Assert.Null(fila.FuenteMonto);
        Assert.NotNull(fila.MotivoSinMonto);
    }

    // ── Regla 3: reserva → fila ──

    [Fact]
    public void Reserva_activa_produce_fila_con_monto_facturado_sumado_de_sus_filas_de_la_tarea_3()
    {
        var reserva = Reserva("r1", "Reserva SRVAZPRVE01", "Standard_B16ms", "P1Y", "2027-03-15",
            [Consumidor("vmA"), Consumidor("vmB")]);
        var foto = Foto(reservas: [reserva]);
        var modelo = ModeloReservas(filas: [
            VmFila("r1", "vmA", demanda: 400m, reservaMes: 300m, ahorro: 100m),
            VmFila("r1", "vmB", demanda: 380m, reservaMes: 300m, ahorro: 80m),
        ]);

        var (filas, ejes) = Calcular(fotoReservas: foto, reservasFacturadas: modelo);

        var fila = Assert.Single(filas);
        Assert.Equal("reserva", fila.Fuente);
        Assert.Equal("Reserva SRVAZPRVE01", fila.Oportunidad);
        Assert.Equal(CategoriaEjecutado.Reservas, fila.Categoria);
        // ExpiresOn 2027-03-15 menos P1Y = inicio 2026-03; MesFin = mes de ExpiresOn.
        Assert.Equal("2026-03", fila.MesEjecucion);
        Assert.Equal("2027-03", fila.MesFin);
        Assert.Equal(180m, fila.MontoMensual); // 100 + 80
        Assert.Equal("facturado", fila.FuenteMonto);
        Assert.Null(fila.MotivoSinMonto);
        Assert.Equal("declarada", fila.Autoria);
        Assert.True(ejes.ReservasMedidas);
    }

    [Fact]
    public void Reserva_sin_filas_con_ahorro_publica_fila_con_motivo_y_sin_monto()
    {
        var reserva = Reserva("r1", "Reserva sin evolución", "Standard_B16ms", "P1Y", "2027-03-15", [Consumidor("vmA")]);
        var foto = Foto(reservas: [reserva]);
        var modelo = ModeloReservas(filas: [VmFila("r1", "vmA", demanda: null, reservaMes: 331.72m, ahorro: null)]);

        var (filas, _) = Calcular(fotoReservas: foto, reservasFacturadas: modelo);

        var fila = Assert.Single(filas);
        Assert.Null(fila.MontoMensual);
        Assert.Null(fila.FuenteMonto);
        Assert.NotNull(fila.MotivoSinMonto);
    }

    [Fact]
    public void Reserva_sin_inicio_derivable_se_descarta_de_las_filas_y_cuenta_en_el_motivo_del_eje()
    {
        // Term ausente: InicioDeReserva no puede derivarse (AhorroReservasCalculador.AniosDelTermino
        // exige el formato "P{n}Y"; sin term no hay nada que restar de ExpiresOn).
        var reserva = Reserva("r1", "Reserva con termino raro", "Standard_B16ms", term: null, "2027-03-15", [Consumidor("vmA")]);
        var foto = Foto(reservas: [reserva]);

        var (filas, ejes) = Calcular(fotoReservas: foto, reservasFacturadas: ModeloReservas());

        Assert.Empty(filas);
        Assert.True(ejes.ReservasMedidas);
        Assert.Contains("inicio", ejes.ReservasMotivo, StringComparison.OrdinalIgnoreCase);
    }

    // ── Regla 4: monto facturado (barrido y matriz) — caso evidente del brief ──

    [Fact]
    public void Delta_facturado_es_el_promedio_de_meses_completos_antes_menos_el_de_despues()
    {
        var barrido = new RegistroBarrido(true, null,
            [BarridoFila("orphaned_disks", "s1", "rg1", "vm1", new DateTime(2026, 4, 15))]);
        var facturacion = new[]
        {
            Fila("s1", "rg1", "vm1", 100m, 2026, 1),
            Fila("s1", "rg1", "vm1", 100m, 2026, 2),
            Fila("s1", "rg1", "vm1", 100m, 2026, 3),
            Fila("s1", "rg1", "vm1", 40m, 2026, 5),
            Fila("s1", "rg1", "vm1", 40m, 2026, 6),
        };

        var (filas, _) = Calcular(barrido: barrido, facturacion: facturacion);

        var fila = Assert.Single(filas);
        Assert.Equal(60.00m, fila.MontoMensual);
        Assert.Equal("facturado", fila.FuenteMonto);
    }

    [Fact]
    public void Meses_forzados_como_parciales_se_excluyen_del_promedio_de_meses_completos()
    {
        var barrido = new RegistroBarrido(true, null,
            [BarridoFila("orphaned_disks", "s1", "rg1", "vm1", new DateTime(2026, 4, 15))]);
        var facturacion = new[]
        {
            Fila("s1", "rg1", "vm1", 100m, 2026, 1),
            Fila("s1", "rg1", "vm1", 100m, 2026, 2),
            Fila("s1", "rg1", "vm1", 999m, 2026, 3), // forzado parcial: se excluye del promedio "antes"
            Fila("s1", "rg1", "vm1", 40m, 2026, 5),
        };
        var contexto = ContextoAmplio with { MesesParcialesForzados = ["2026-03"] };

        var (filas, _) = Calcular(barrido: barrido, facturacion: facturacion, contexto: contexto);

        var fila = Assert.Single(filas);
        Assert.Equal(60.00m, fila.MontoMensual); // (100+100)/2=100 antes, 40 despues -> 60
    }

    // ── Regla 5: precedencia del monto (barrido) ──

    [Fact]
    public void Sin_delta_medible_el_barrido_cae_al_estimado_si_es_positivo()
    {
        var barrido = new RegistroBarrido(true, null,
            [BarridoFila("orphaned_disks", "s1", "rg1", "vm1", new DateTime(2026, 4, 15), estimatedMonthlySavings: 25m)]);
        // Sin facturacion: el delta no es medible.

        var (filas, _) = Calcular(barrido: barrido);

        var fila = Assert.Single(filas);
        Assert.Equal(25m, fila.MontoMensual);
        Assert.Equal("estimado", fila.FuenteMonto);
        Assert.Null(fila.MotivoSinMonto);
    }

    [Fact]
    public void Delta_negativo_nunca_entra_como_facturado_y_cae_al_estimado()
    {
        var barrido = new RegistroBarrido(true, null,
            [BarridoFila("orphaned_disks", "s1", "rg1", "vm1", new DateTime(2026, 4, 15), estimatedMonthlySavings: 10m)]);
        var facturacion = new[]
        {
            Fila("s1", "rg1", "vm1", 40m, 2026, 1), // antes mas barato
            Fila("s1", "rg1", "vm1", 100m, 2026, 5), // despues mas caro: delta negativo
        };

        var (filas, _) = Calcular(barrido: barrido, facturacion: facturacion);

        var fila = Assert.Single(filas);
        Assert.Equal(10m, fila.MontoMensual);
        Assert.Equal("estimado", fila.FuenteMonto); // nunca "facturado" con delta <= 0
    }

    [Fact]
    public void Sin_delta_y_sin_estimado_la_fila_queda_sin_monto_con_motivo_concreto()
    {
        var barrido = new RegistroBarrido(true, null,
            [BarridoFila("orphaned_disks", "s1", "rg1", "vm1", new DateTime(2026, 4, 15))]);

        var (filas, _) = Calcular(barrido: barrido);

        var fila = Assert.Single(filas);
        Assert.Null(fila.MontoMensual);
        Assert.Null(fila.FuenteMonto);
        Assert.Equal("La facturación no muestra reducción y el barrido no estimó ahorro.", fila.MotivoSinMonto);
    }

    // ── Regla 6: matriz sin delta ──

    [Fact]
    public void Matriz_sin_delta_medible_publica_fila_sin_monto_con_motivo_de_postura()
    {
        var hallazgos = new[] { MatrizFila("s1", "rg1", "vm1", new DateOnly(2026, 5, 3)) };
        // Sin facturacion: la matriz no tiene delta ni estimacion propia.

        var (filas, _) = Calcular(hallazgosMatriz: hallazgos);

        var fila = Assert.Single(filas);
        Assert.Null(fila.MontoMensual);
        Assert.Null(fila.FuenteMonto);
        Assert.Contains("Postura", fila.MotivoSinMonto);
    }

    // ── Regla 7: dedup entre fuentes ──

    [Fact]
    public void Barrido_y_matriz_resueltos_el_mismo_mes_para_el_mismo_recurso_el_barrido_gana_y_la_matriz_queda_anotada()
    {
        var barrido = new RegistroBarrido(true, null,
            [BarridoFila("orphaned_disks", "s1", "rg1", "vm1", new DateTime(2026, 5, 20), estimatedMonthlySavings: 15m)]);
        var hallazgos = new[] { MatrizFila("s1", "rg1", "vm1", new DateOnly(2026, 5, 3)) };

        var (filas, _) = Calcular(barrido: barrido, hallazgosMatriz: hallazgos);

        Assert.Equal(2, filas.Count); // ninguna se borra en silencio
        var filaBarrido = Assert.Single(filas, f => f.Fuente == "barrido");
        var filaMatriz = Assert.Single(filas, f => f.Fuente == "matriz");
        Assert.Equal(15m, filaBarrido.MontoMensual);
        Assert.Equal("estimado", filaBarrido.FuenteMonto);
        Assert.Null(filaMatriz.MontoMensual);
        Assert.Null(filaMatriz.FuenteMonto);
        Assert.NotNull(filaMatriz.MotivoSinMonto);
        Assert.Contains("barrido", filaMatriz.MotivoSinMonto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recurso_cubierto_por_una_reserva_confirmada_la_reserva_gana_y_el_hallazgo_del_barrido_queda_anotado()
    {
        var reserva = Reserva("r1", "Reserva cubre vm1", "Standard_B16ms", "P1Y", "2027-03-15", [Consumidor("vm1", resourceGroup: "rg1", subscriptionId: "s1")]);
        var foto = Foto(reservas: [reserva]);
        var modelo = ModeloReservas(filas: [VmFila("r1", "vm1", demanda: 400m, reservaMes: 300m, ahorro: 100m)]);
        var barrido = new RegistroBarrido(true, null,
            [BarridoFila("orphaned_disks", "s1", "rg1", "vm1", new DateTime(2026, 4, 15), estimatedMonthlySavings: 20m)]);

        var (filas, _) = Calcular(barrido: barrido, fotoReservas: foto, reservasFacturadas: modelo);

        Assert.Equal(2, filas.Count);
        var filaReserva = Assert.Single(filas, f => f.Fuente == "reserva");
        var filaBarrido = Assert.Single(filas, f => f.Fuente == "barrido");
        Assert.Equal(100m, filaReserva.MontoMensual);
        Assert.Equal("facturado", filaReserva.FuenteMonto);
        Assert.Null(filaBarrido.MontoMensual);
        Assert.NotNull(filaBarrido.MotivoSinMonto);
        Assert.Contains("reserva", filaBarrido.MotivoSinMonto, StringComparison.OrdinalIgnoreCase);
    }

    // ── Regla 7b: dedup dentro de la misma fuente (mismo recurso+mes, dos filas) ──

    [Fact]
    public void Dos_filas_de_barrido_mismo_recurso_y_mes_solo_la_primera_por_checkId_reclama_el_delta_facturado()
    {
        var barrido = new RegistroBarrido(true, null,
        [
            // "old_snapshots" < "orphaned_disks" < "zzz_check_sin_estimado" en orden ordinal: gana old_snapshots.
            BarridoFila("orphaned_disks", "s1", "rg1", "vm1", new DateTime(2026, 4, 20), estimatedMonthlySavings: 15m),
            BarridoFila("old_snapshots", "s1", "rg1", "vm1", new DateTime(2026, 4, 10)),
            BarridoFila("zzz_check_sin_estimado", "s1", "rg1", "vm1", new DateTime(2026, 4, 5)),
        ]);
        var facturacion = new[]
        {
            Fila("s1", "rg1", "vm1", 100m, 2026, 1),
            Fila("s1", "rg1", "vm1", 100m, 2026, 2),
            Fila("s1", "rg1", "vm1", 100m, 2026, 3),
            Fila("s1", "rg1", "vm1", 40m, 2026, 5),
            Fila("s1", "rg1", "vm1", 40m, 2026, 6),
        };

        var (filas, _) = Calcular(barrido: barrido, facturacion: facturacion);

        Assert.Equal(3, filas.Count); // ninguna se borra en silencio

        var ganadora = Assert.Single(filas, f => f.Oportunidad == "Snapshots antiguos"); // old_snapshots
        Assert.Equal(60.00m, ganadora.MontoMensual);
        Assert.Equal("facturado", ganadora.FuenteMonto);
        Assert.Null(ganadora.MotivoSinMonto);

        var conEstimadoPropio = Assert.Single(filas, f => f.Oportunidad == "Discos administrados no conectados"); // orphaned_disks
        Assert.Equal(15m, conEstimadoPropio.MontoMensual);
        Assert.Equal("estimado", conEstimadoPropio.FuenteMonto);
        Assert.Null(conEstimadoPropio.MotivoSinMonto);

        var sinEstimadoPropio = Assert.Single(filas, f => f.Oportunidad == "zzz_check_sin_estimado"); // checkId crudo, no registrado
        Assert.Null(sinEstimadoPropio.MontoMensual);
        Assert.Null(sinEstimadoPropio.FuenteMonto);
        Assert.NotNull(sinEstimadoPropio.MotivoSinMonto);
        Assert.Contains("Snapshots antiguos", sinEstimadoPropio.MotivoSinMonto);

        // El delta (60) se reclama UNA sola vez; el 15 es el estimado propio de la fila perdedora.
        Assert.Equal(75.00m, filas.Sum(f => f.MontoMensual ?? 0m));
    }

    [Fact]
    public void Dos_hallazgos_de_matriz_mismo_recurso_y_mes_solo_el_primero_por_matrixCode_reclama_el_delta_facturado()
    {
        var hallazgos = new[]
        {
            MatrizFila("s1", "rg1", "vm1", new DateOnly(2026, 4, 20), hallazgo: "Habilitar backup", matrixCode: "5.1"),
            MatrizFila("s1", "rg1", "vm1", new DateOnly(2026, 4, 10), hallazgo: "Cerrar puertos abiertos", matrixCode: "3.2"),
        };
        var facturacion = new[]
        {
            Fila("s1", "rg1", "vm1", 100m, 2026, 1),
            Fila("s1", "rg1", "vm1", 100m, 2026, 2),
            Fila("s1", "rg1", "vm1", 100m, 2026, 3),
            Fila("s1", "rg1", "vm1", 40m, 2026, 5),
            Fila("s1", "rg1", "vm1", 40m, 2026, 6),
        };

        var (filas, _) = Calcular(hallazgosMatriz: hallazgos, facturacion: facturacion);

        Assert.Equal(2, filas.Count); // ninguna se borra en silencio

        var ganadora = Assert.Single(filas, f => f.Oportunidad == "Cerrar puertos abiertos"); // matrixCode "3.2"
        Assert.Equal(60.00m, ganadora.MontoMensual);
        Assert.Equal("facturado", ganadora.FuenteMonto);
        Assert.Null(ganadora.MotivoSinMonto);

        var perdedora = Assert.Single(filas, f => f.Oportunidad == "Habilitar backup"); // matrixCode "5.1"
        Assert.Null(perdedora.MontoMensual);
        Assert.Null(perdedora.FuenteMonto);
        Assert.NotNull(perdedora.MotivoSinMonto);
        Assert.Contains("Cerrar puertos abiertos", perdedora.MotivoSinMonto);

        // La matriz no tiene estimado propio: el delta se reclama una sola vez y el resto queda en cero.
        Assert.Equal(60.00m, filas.Sum(f => f.MontoMensual ?? 0m));
    }

    // ── Regla 8: ejes ──

    [Fact]
    public void Ejes_reportan_medicion_del_barrido_y_reservas_y_cuenta_las_indeterminadas()
    {
        var barrido = new RegistroBarrido(true, null,
        [
            BarridoFila("orphaned_disks", "s1", "rg1", "vm1", new DateTime(2026, 4, 15), resolvedByKind: "manual"),
            BarridoFila("old_snapshots", "s1", "rg2", "snap1", new DateTime(2026, 4, 20), resolvedByKind: null),
        ]);

        var (_, ejes) = Calcular(barrido: barrido, reservasFacturadas: ModeloReservas(medido: false, motivo: "sin evolución"),
            fotoReservas: Foto(medido: true, motivo: "leidas"));

        Assert.True(ejes.BarridoMedido);
        Assert.Null(ejes.BarridoMotivo);
        Assert.False(ejes.ReservasMedidas);
        Assert.Equal("sin evolución", ejes.ReservasMotivo);
        Assert.Equal(1, ejes.Indeterminadas);
    }

    [Fact]
    public void Ejes_sin_barrido_corrido_declaran_no_medido_con_el_motivo_del_recolector()
    {
        var (filas, ejes) = Calcular(barrido: RegistroBarrido.SinBarrido());

        Assert.Empty(filas);
        Assert.False(ejes.BarridoMedido);
        Assert.Equal("El cliente no tiene ningún barrido de optimización corrido.", ejes.BarridoMotivo);
    }
}
