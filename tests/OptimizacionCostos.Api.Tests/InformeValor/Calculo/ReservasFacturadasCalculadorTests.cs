using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Calculo.Ejecutado;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Tarea 3 del plan de la entrega 6: la tabla "reservas contra la propia factura" (spec, tabla por
/// VM del HTML de referencia). El precio de la reserva no existe en BITCOST (tabla de hechos): sale
/// de las líneas <c>IsReservation=true</c> del archivo de evolución, parseando
/// <c>"Reserved VM Instance, SKU, región, término"</c>. Pura y sin reloj (ver
/// <c>SinRelojDelSistemaTests</c>, que escanea esta carpeta completa).
/// </summary>
public sealed class ReservasFacturadasCalculadorTests
{
    private static int _n;

    private static FacturacionRow Fila(
        string? subscriptionId, string? resourceGroup, string? resourceName, decimal pvp, short year, byte month,
        string? subscriptionName = null) =>
        new(Hash: $"h{++_n}", Tenant: null, SubscriptionName: subscriptionName, SubscriptionId: subscriptionId,
            ResourceGroup: resourceGroup, ResourceName: resourceName, CostCenter: null, Category: "Cómputo",
            Subcategory: null, Service: null, Quantity: null, Unit: null, Rate: null,
            Pvp: pvp, Year: year, Month: month);

    private static EvolucionRow LineaReserva(string sku, string region, string terminoTexto, decimal pvp, short year, byte month) =>
        new(NaturalKeyHash: $"e{++_n}", Category: "Reservas", Subcategory: null,
            ResourceName: $"Reserved VM Instance, {sku}, {region}, {terminoTexto}",
            IsReservation: true, Pvp: pvp, PeriodYear: year, PeriodMonth: month);

    private static ConsumidorReserva Consumidor(
        string resourceName, string? resourceGroup = "rg1", string? subscriptionId = "s1", string? sku = "Standard_B16ms") =>
        new(InstanceId: $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{resourceName}",
            ResourceName: resourceName, ResourceGroup: resourceGroup, SubscriptionId: subscriptionId,
            SkuName: sku, UsedHours: 720, LastSeen: "2026-02-28", DaysSeen: 28);

    private static ReservaActiva Reserva(
        string? id, string? nombre, string? producto, string? region, string? term, string expiresOn,
        IReadOnlyList<ConsumidorReserva> consumidores, bool expiring = false) =>
        new(ReservationId: id, Nombre: nombre, Producto: producto, Region: region, Cantidad: consumidores.Count,
            Term: term, TermLabel: term, ExpiresOn: expiresOn, DaysRemaining: 300, Expiring: expiring,
            UtilizationLast: "90%", Utilization7d: "88%", Consumidores: consumidores,
            UnidadesEstimadas: 0, ConsumidoresNoLeidos: false);

    private static FotoReservas Foto(bool medido = true, string motivo = "ok", IReadOnlyList<ReservaActiva>? reservas = null) =>
        new(Medido: medido, Motivo: motivo, Errores: [], AlertDays: 30,
            CapturadaEn: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), Reservas: reservas ?? []);

    /// <summary>Rango amplio: ninguno de los fixtures necesita ejercitar el filtro D0 de por sí, así
    /// que un contexto ancho evita que <c>ConsumoCalculador.EnRango</c> excluya una fila por
    /// accidente.</summary>
    private static readonly ContextoInformeValor ContextoAmplio =
        new(new DateOnly(2020, 1, 1), new DateOnly(2030, 12, 31), new DateOnly(2026, 3, 1), null);

    // ── Regla 5: sin foto medida, el modelo tampoco lo está ──

    [Fact]
    public void Sin_foto_medida_el_modelo_degrada_con_el_motivo_de_la_foto()
    {
        var foto = Foto(medido: false, motivo: "sin credenciales activas");

        var modelo = ReservasFacturadasCalculador.Calcular(foto, [], [], ContextoAmplio);

        Assert.False(modelo.Medido);
        Assert.Equal("sin credenciales activas", modelo.Motivo);
        Assert.Empty(modelo.Filas);
        Assert.Empty(modelo.SinLineaEnEvolucion);
        Assert.Equal(0m, modelo.TotalDemanda);
        Assert.Equal(0m, modelo.TotalReserva);
        Assert.Equal(0m, modelo.TotalAhorro);
        Assert.Equal(0m, modelo.AhorroAnualizado);
    }

    /// <summary>La otra mitad de la regla 5: la foto SÍ midió y SÍ trae reservas activas, pero el
    /// archivo de evolución no trae ninguna línea "Reserved VM Instance" dentro del rango — nunca se
    /// publica una tabla vacía que se vea como "el cliente no tiene reservas".</summary>
    [Fact]
    public void Foto_medida_con_reservas_pero_evolucion_sin_ninguna_linea_de_reserva_degrada()
    {
        var reserva = Reserva("r1", "Reserva sin evolución", "Standard_B16ms", "US East 2", "P1Y", "2027-03-15",
            [Consumidor("vm1")]);
        var foto = Foto(reservas: [reserva]);

        var modelo = ReservasFacturadasCalculador.Calcular(foto, evolucion: [], facturacion: [], ContextoAmplio);

        Assert.False(modelo.Medido);
        Assert.Contains("evolución", modelo.Motivo, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(modelo.Filas);
    }

    // ── Regla 1 + Regla 3: el caso feliz, con los números de SRVAZPRVE01 del HTML de referencia ──

    /// <summary>Una reserva, un consumidor confirmado, una sola línea de evolución sin compartir.
    /// Inicio de la reserva: 2027-03-15 (ExpiresOn) - 1 año (Term "P1Y") = 2026-03 → el último mes
    /// completo ANTERIOR es 2026-02 (482.13; enero trae un valor distinto para probar que se toma el
    /// más cercano, no el primero que aparece). La línea de evolución solo factura un mes dentro del
    /// rango (331.72): con menos de tres meses, la regla 1 usa el promedio, que de un solo valor es
    /// el valor mismo.</summary>
    [Fact]
    public void Calcula_demanda_reserva_y_ahorro_del_caso_feliz_sin_compartir()
    {
        var reserva = Reserva("r1", "Reserva SRVAZPRVE01", "Standard_B16ms", "US East 2", "P1Y", "2027-03-15",
            [Consumidor("SRVAZPRVE01")]);
        var foto = Foto(reservas: [reserva]);
        var evolucion = new[] { LineaReserva("Standard_B16ms", "US East 2", "1 Year", pvp: 331.72m, year: 2026, month: 3) };
        var facturacion = new[]
        {
            Fila("s1", "rg1", "SRVAZPRVE01", pvp: 999.99m, year: 2026, month: 1), // decoy: no es el mes mas cercano
            Fila("s1", "rg1", "SRVAZPRVE01", pvp: 482.13m, year: 2026, month: 2), // el ultimo mes completo antes de 2026-03
        };

        var modelo = ReservasFacturadasCalculador.Calcular(foto, evolucion, facturacion, ContextoAmplio);

        var fila = Assert.Single(modelo.Filas);
        Assert.Equal("SRVAZPRVE01", fila.Vm);
        Assert.Equal("Standard_B16ms", fila.Sku);
        Assert.Equal(482.13m, fila.PorDemandaMes);
        Assert.Equal(331.72m, fila.ReservaMes);
        Assert.Equal(150.41m, fila.AhorroMes);
        Assert.False(fila.Compartida);
        Assert.Equal("2027-03-15", fila.Vence);
        Assert.False(fila.PorVencer);
        Assert.Null(fila.Nota);

        Assert.True(modelo.Medido);
        Assert.Equal(482.13m, modelo.TotalDemanda);
        Assert.Equal(331.72m, modelo.TotalReserva);
        Assert.Equal(150.41m, modelo.TotalAhorro);
        Assert.Equal(1804.92m, modelo.AhorroAnualizado); // 150.41 * 12
        Assert.Empty(modelo.SinLineaEnEvolucion);
    }

    // ── Regla 1: el mapa de término es literal, "3 Years" tambien matchea (no solo "1 Year") ──

    [Fact]
    public void El_termino_de_tres_anios_tambien_matchea_contra_su_mapeo_literal()
    {
        var reserva = Reserva("r1", "Reserva 3 años", "Standard_D4s_v5", "eastus", "P3Y", "2029-01-10",
            [Consumidor("vmTresAnios", sku: "Standard_D4s_v5")]);
        var foto = Foto(reservas: [reserva]);
        var evolucion = new[] { LineaReserva("Standard_D4s_v5", "eastus", "3 Years", pvp: 200m, year: 2026, month: 1) };

        var modelo = ReservasFacturadasCalculador.Calcular(foto, evolucion, [], ContextoAmplio);

        var fila = Assert.Single(modelo.Filas);
        Assert.Equal(200m, fila.ReservaMes);
        Assert.Empty(modelo.SinLineaEnEvolucion);
    }

    // ── Regla 2: reservas compartidas por el mismo SKU+término prorratean entre sus VM ──

    /// <summary>Dos reservas DISTINTAS (no solo dos consumidores de la misma reserva) del mismo
    /// SKU+término matchean la MISMA línea de evolución: el cargo mensual de esa línea se reparte
    /// entre las dos VM confirmadas (una por reserva), mitad para cada una, y las dos quedan
    /// marcadas <see cref="ReservaVmFila.Compartida"/>.</summary>
    [Fact]
    public void Dos_reservas_del_mismo_sku_y_termino_prorratean_el_cargo_de_la_linea_compartida()
    {
        var reservaA = Reserva("rA", "Reserva A", "Standard_B16ms", "US East 2", "P1Y", "2027-06-01",
            [Consumidor("vmA")]);
        var reservaB = Reserva("rB", "Reserva B", "Standard_B16ms", "US East 2", "P1Y", "2027-06-01",
            [Consumidor("vmB", resourceGroup: "rg2", subscriptionId: "s1")]);
        var foto = Foto(reservas: [reservaA, reservaB]);
        var evolucion = new[] { LineaReserva("Standard_B16ms", "US East 2", "1 Year", pvp: 600m, year: 2026, month: 6) };

        var modelo = ReservasFacturadasCalculador.Calcular(foto, evolucion, [], ContextoAmplio);

        Assert.Equal(2, modelo.Filas.Count);
        Assert.All(modelo.Filas, f => Assert.True(f.Compartida));
        Assert.All(modelo.Filas, f => Assert.Equal(300m, f.ReservaMes)); // 600 / 2 VM
        Assert.Contains(modelo.Filas, f => f.Vm == "vmA");
        Assert.Contains(modelo.Filas, f => f.Vm == "vmB");
    }

    // ── Regla 3: sin mes base en BITCOST antes del inicio, la fila se publica igual con nota ──

    [Fact]
    public void Sin_mes_base_en_facturacion_la_fila_se_publica_con_demanda_y_ahorro_nulos_y_nota()
    {
        var reserva = Reserva("r1", "Reserva sin mes base", "Standard_B16ms", "US East 2", "P1Y", "2027-03-15",
            [Consumidor("vmSinFacturacion")]);
        var foto = Foto(reservas: [reserva]);
        var evolucion = new[] { LineaReserva("Standard_B16ms", "US East 2", "1 Year", pvp: 331.72m, year: 2026, month: 3) };

        // facturacion vacia a proposito: el consumidor no aparece en BITCOST en absoluto.
        var modelo = ReservasFacturadasCalculador.Calcular(foto, evolucion, facturacion: [], ContextoAmplio);

        var fila = Assert.Single(modelo.Filas);
        Assert.Null(fila.PorDemandaMes);
        Assert.Null(fila.AhorroMes);
        Assert.NotNull(fila.Nota);
        Assert.Equal(331.72m, fila.ReservaMes); // la reserva sigue publicandose, solo la demanda falta

        // Los totales excluyen la fila sin ahorro calculable (regla 6).
        Assert.Equal(0m, modelo.TotalDemanda);
        Assert.Equal(0m, modelo.TotalReserva);
        Assert.Equal(0m, modelo.TotalAhorro);
    }

    // ── Regla 4: una reserva activa sin match en evolucion va a SinLineaEnEvolucion, nunca inventa cargo ──

    [Fact]
    public void Una_reserva_sin_match_en_evolucion_se_publica_en_sin_linea_en_evolucion()
    {
        var reservaSinLinea = Reserva("r1", "Reserva Fabric sin VM", "Microsoft Fabric F64", null, "P1Y", "2027-05-01",
            [Consumidor("recursoFabric", sku: "F64")]);
        var foto = Foto(reservas: [reservaSinLinea]);
        // Una linea de evolucion presente, pero de un SKU totalmente distinto: evita que la regla 5
        // (evolucion sin NINGUNA linea) degrade el modelo entero antes de llegar a la regla 4.
        var evolucion = new[] { LineaReserva("Standard_B16ms", "US East 2", "1 Year", pvp: 100m, year: 2026, month: 1) };

        var modelo = ReservasFacturadasCalculador.Calcular(foto, evolucion, [], ContextoAmplio);

        Assert.True(modelo.Medido);
        Assert.Empty(modelo.Filas);
        Assert.Equal("Reserva Fabric sin VM", Assert.Single(modelo.SinLineaEnEvolucion));
    }

    // ── Regla 6: los totales son la suma exacta de las filas ya redondeadas, no una re-suma redondeada ──

    [Fact]
    public void Los_totales_suman_solo_filas_con_ahorro_calculable_y_el_anualizado_es_el_ahorro_por_doce()
    {
        var reservaConAhorro = Reserva("r1", "Con ahorro", "Standard_B16ms", "US East 2", "P1Y", "2027-03-15",
            [Consumidor("vmConAhorro")]);
        var reservaSinAhorro = Reserva("r2", "Sin ahorro", "Standard_D4s_v5", "eastus", "P1Y", "2027-04-01",
            [Consumidor("vmSinFacturar", subscriptionId: "s2", sku: "Standard_D4s_v5")]);
        var foto = Foto(reservas: [reservaConAhorro, reservaSinAhorro]);
        var evolucion = new[]
        {
            LineaReserva("Standard_B16ms", "US East 2", "1 Year", pvp: 331.72m, year: 2026, month: 3),
            LineaReserva("Standard_D4s_v5", "eastus", "1 Year", pvp: 50m, year: 2026, month: 4),
        };
        var facturacion = new[] { Fila("s1", "rg1", "vmConAhorro", pvp: 482.13m, year: 2026, month: 2) };

        var modelo = ReservasFacturadasCalculador.Calcular(foto, evolucion, facturacion, ContextoAmplio);

        Assert.Equal(2, modelo.Filas.Count);
        Assert.Equal(482.13m, modelo.TotalDemanda); // solo la fila con ahorro calculable
        Assert.Equal(331.72m, modelo.TotalReserva);
        Assert.Equal(150.41m, modelo.TotalAhorro);
        Assert.Equal(1804.92m, modelo.AhorroAnualizado);
    }

    // ── Regla 1: el SKU matchea por igualdad exacta, nunca por substring ──

    /// <summary>"Standard_A1" es substring literal de "Standard_A1_v2" pero son familias de VM
    /// distintas con precio distinto: con Contains, la reserva de la v2 matchearía la línea SIN v2 y
    /// facturaría dinero equivocado en silencio. Dos líneas con el mismo término, una reserva cuyo
    /// SKU es "Standard_A1_v2": debe matchear SOLO la línea _v2.</summary>
    [Fact]
    public void El_sku_matchea_por_igualdad_exacta_no_por_substring()
    {
        var reserva = Reserva("r1", "Reserva A1 v2", "Standard_A1_v2", "eastus", "P1Y", "2027-01-10",
            [Consumidor("vmA1v2", sku: "Standard_A1_v2")]);
        var foto = Foto(reservas: [reserva]);
        var evolucion = new[]
        {
            LineaReserva("Standard_A1", "eastus", "1 Year", pvp: 50m, year: 2026, month: 1),
            LineaReserva("Standard_A1_v2", "eastus", "1 Year", pvp: 90m, year: 2026, month: 1),
        };

        var modelo = ReservasFacturadasCalculador.Calcular(foto, evolucion, [], ContextoAmplio);

        var fila = Assert.Single(modelo.Filas);
        Assert.Equal(90m, fila.ReservaMes); // la linea de "Standard_A1" (sin v2) nunca debio matchear
        Assert.Empty(modelo.SinLineaEnEvolucion);
    }

    // ── Regla 1: la region desempata por token entre formato ARM y formato de visualizacion ──

    /// <summary>Dos lineas con el mismo SKU+termino, una en "US East 2" y otra en "West Europe": la
    /// reserva activa trae la region en formato ARM ("eastus2"). El desempate tokeniza el nombre de
    /// visualizacion ("us","east","2") y exige que todos los tokens aparezcan en el ARM en minuscula:
    /// matchea la primera linea, nunca la de Europa.</summary>
    [Fact]
    public void La_region_desempata_por_token_entre_formato_arm_y_formato_de_visualizacion()
    {
        var reserva = Reserva("r1", "Reserva con desempate de region", "Standard_B16ms", "eastus2", "P1Y", "2027-02-01",
            [Consumidor("vmEastUs2")]);
        var foto = Foto(reservas: [reserva]);
        var evolucion = new[]
        {
            LineaReserva("Standard_B16ms", "US East 2", "1 Year", pvp: 300m, year: 2026, month: 1),
            LineaReserva("Standard_B16ms", "West Europe", "1 Year", pvp: 700m, year: 2026, month: 1),
        };

        var modelo = ReservasFacturadasCalculador.Calcular(foto, evolucion, [], ContextoAmplio);

        var fila = Assert.Single(modelo.Filas);
        Assert.Equal(300m, fila.ReservaMes);
        Assert.Empty(modelo.SinLineaEnEvolucion);
    }

    /// <summary>El desempate por token es una heuristica, no una identidad: "US East" (sin el "2") y
    /// "US East 2" tokenizan ambas dentro de "eastus2" ("us","east" son substring igual que
    /// "us","east","2"). Cuando el desempate no logra separar las candidatas, la regla 2 del review
    /// manda: NO se elige candidatas[0] a ciegas, la reserva va a SinLineaEnEvolucion con una nota que
    /// nombra la ambiguedad.</summary>
    [Fact]
    public void Cuando_el_desempate_de_region_no_separa_las_candidatas_no_se_elige_a_ciegas()
    {
        var reserva = Reserva("r1", "Reserva ambigua", "Standard_B16ms", "eastus2", "P1Y", "2027-02-01",
            [Consumidor("vmAmbigua")]);
        var foto = Foto(reservas: [reserva]);
        var evolucion = new[]
        {
            LineaReserva("Standard_B16ms", "US East 2", "1 Year", pvp: 300m, year: 2026, month: 1),
            LineaReserva("Standard_B16ms", "US East", "1 Year", pvp: 310m, year: 2026, month: 1),
        };

        var modelo = ReservasFacturadasCalculador.Calcular(foto, evolucion, [], ContextoAmplio);

        Assert.Empty(modelo.Filas);
        var nota = Assert.Single(modelo.SinLineaEnEvolucion);
        Assert.Contains("2", nota); // 2 lineas candidatas
        Assert.Contains("no se elige a ciegas", nota);
    }

    // ── Regla 1: con 3+ meses completos en rango, ReservaMes usa la mediana, no el promedio ──

    /// <summary>Tres meses de la misma linea: 300, 900, 320 (el 900 es el mes de compra prorrateado
    /// distorsionando hacia arriba). El promedio simple daria 506.67; la mediana (320) es la que la
    /// regla 1 exige a partir de 3 meses, justo para absorber ese atipico sin tener que identificar
    /// cual mes fue.</summary>
    [Fact]
    public void Con_tres_meses_completos_en_rango_reserva_mes_usa_la_mediana_no_el_promedio()
    {
        var reserva = Reserva("r1", "Reserva con outlier de prorrateo", "Standard_B16ms", "US East 2", "P1Y", "2027-03-15",
            [Consumidor("vmMediana")]);
        var foto = Foto(reservas: [reserva]);
        var evolucion = new[]
        {
            LineaReserva("Standard_B16ms", "US East 2", "1 Year", pvp: 300m, year: 2026, month: 1),
            LineaReserva("Standard_B16ms", "US East 2", "1 Year", pvp: 900m, year: 2026, month: 2),
            LineaReserva("Standard_B16ms", "US East 2", "1 Year", pvp: 320m, year: 2026, month: 3),
        };

        var modelo = ReservasFacturadasCalculador.Calcular(foto, evolucion, [], ContextoAmplio);

        var fila = Assert.Single(modelo.Filas);
        Assert.Equal(320m, fila.ReservaMes); // mediana de 300/900/320, nunca el promedio 506.67
    }

    // ── ReservationId de la fila viaja desde la ReservaActiva que la origino (Tarea 4 lo necesita) ──

    [Fact]
    public void La_fila_lleva_el_reservation_id_de_la_reserva_que_la_origino()
    {
        var reserva = Reserva("r-abc-123", "Reserva con id", "Standard_B16ms", "US East 2", "P1Y", "2027-03-15",
            [Consumidor("vmConId")]);
        var foto = Foto(reservas: [reserva]);
        var evolucion = new[] { LineaReserva("Standard_B16ms", "US East 2", "1 Year", pvp: 100m, year: 2026, month: 1) };

        var modelo = ReservasFacturadasCalculador.Calcular(foto, evolucion, [], ContextoAmplio);

        var fila = Assert.Single(modelo.Filas);
        Assert.Equal("r-abc-123", fila.ReservationId);
    }

    // ── ConsumidoresNoLeidos viaja desde la foto (mismo criterio de D9 que el resto del modulo) ──

    [Fact]
    public void Cuenta_las_reservas_cuyos_consumidores_no_se_pudieron_leer()
    {
        var reservaFallida = new ReservaActiva(
            ReservationId: "r1", Nombre: "Reserva con falla", Producto: "Standard_B16ms", Region: "US East 2",
            Cantidad: 2, Term: "P1Y", TermLabel: "1 año", ExpiresOn: "2027-03-15", DaysRemaining: 300,
            Expiring: false, UtilizationLast: "n/d", Utilization7d: "n/d", Consumidores: [],
            UnidadesEstimadas: 0, ConsumidoresNoLeidos: true);
        var foto = Foto(reservas: [reservaFallida]);
        var evolucion = new[] { LineaReserva("Standard_B16ms", "US East 2", "1 Year", pvp: 100m, year: 2026, month: 1) };

        var modelo = ReservasFacturadasCalculador.Calcular(foto, evolucion, [], ContextoAmplio);

        Assert.Equal(1, modelo.ConsumidoresNoLeidos);
    }
}
