using System.Text.Json;
using OptimizacionCostos.Api.Features.InformeValor;
using OptimizacionCostos.Api.Features.InformeValor.Calculo;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor.Calculo;

/// <summary>
/// Tarea 5 del plan de la entrega 2d (E0, E9): el ensamblado de los tres baldes de la atribución
/// dentro de <c>InformeValorEnsamblador.Ensamblar</c> — a diferencia de
/// <c>AhorroReservasCalculadorTests</c>/<c>AtribucionCalculadorTests</c>, que prueban cada balde por
/// separado, esta clase prueba el CABLEADO: que <see cref="ConsumoModelo.MesesParciales"/> llega a
/// los dos bloques de la ventana fija, que el conjunto de recursos que se excluye de los baldes 2 y
/// 3 es el que <see cref="AhorroReservasCalculador"/> ya filtró por E9 (nunca "confirmada" a secas),
/// y que la variación total resultante es exactamente la suma de los tres baldes ya redondeados.
/// Datos sintéticos (regla dura del encargo): nada de nombres de recurso ni montos de cliente reales.
/// </summary>
public sealed class VariacionConsumoEnsambladoTests
{
    private static int _n;

    private static FacturacionRow Factura(string resourceName, decimal pvp, int mes) => new(
        Hash: $"h{++_n}", Tenant: null, SubscriptionName: "Suscripción de prueba", SubscriptionId: "sub-1",
        ResourceGroup: "rg-1", ResourceName: resourceName, CostCenter: null, Category: "Cómputo",
        Subcategory: null, Service: null, Quantity: null, Unit: null, Rate: null,
        Pvp: pvp, Year: 2026, Month: (byte)mes);

    /// <summary>Seis filas para un recurso "normal" (sin reserva): <paramref name="baseVals"/> en
    /// enero-marzo, <paramref name="finVals"/> en abril-junio. Un valor <c>null</c> en la posición
    /// significa "sin fila ese mes" (el recurso no facturó), no un cero explícito.</summary>
    private static IEnumerable<FacturacionRow> Filas(string recurso, decimal?[] valoresPorMes)
    {
        for (var i = 0; i < valoresPorMes.Length; i++)
            if (valoresPorMes[i] is { } v)
                yield return Factura(recurso, v, mes: i + 1);
    }

    private static HallazgoResueltoFila Hallazgo(string recurso, DateOnly resueltoEl) => new(
        SubscriptionId: "sub-1", SubscriptionName: "Suscripción de prueba", ResourceGroup: "rg-1",
        ResourceName: recurso, ResolvedAt: resueltoEl, MatrixCode: "1.1",
        Hallazgo: "Hallazgo de prueba", PillarNumber: 1);

    private static ConsumidorReserva Consumidor(string recurso) =>
        new(InstanceId: $"/subscriptions/sub-1/resourceGroups/rg-1/providers/x/{recurso}",
            ResourceName: recurso, ResourceGroup: "rg-1", SubscriptionId: "sub-1",
            SkuName: "Standard_D2s_v5", UsedHours: 100, LastSeen: "2026-06-30", DaysSeen: 30);

    /// <param name="expiresOn">Junto con <c>Term="P1Y"</c>, deriva el inicio de la reserva
    /// (ExpiresOn menos un año): quien llama elige la fecha para caer dentro o fuera de la ventana
    /// del informe a propósito (E9).</param>
    private static ReservaActiva Reserva(string id, string recurso, string expiresOn) => new(
        ReservationId: id, Nombre: $"Reserva {id}", Producto: "Standard_D2s_v5", Region: "eastus",
        Cantidad: 1, Term: "P1Y", TermLabel: "1 año", ExpiresOn: expiresOn, DaysRemaining: 300,
        Expiring: false, UtilizationLast: "85%", Utilization7d: "80%",
        Consumidores: [Consumidor(recurso)], UnidadesEstimadas: 0, ConsumidoresNoLeidos: false);

    private static InsumosBd Insumos(IReadOnlyList<HallazgoResueltoFila> hallazgos) => new(
        Advisor: [], Matriz: [], Rbac: [], Retiros: [],
        EstadoRbac: new EstadoRbacResultado(
            DisponibilidadRbac.Completo, new EjesRbac(EstadoCuentaMedido: true, UltimoLoginMedido: true),
            FechaCorrida: new DateTime(2026, 1, 1), Motivo: "completo"),
        SeguridadGestionadaExternamente: false, SeguridadGestionadaNota: null,
        LeidoEn: new DateTime(2026, 1, 1), HallazgosResueltos: hallazgos);

    /// <summary><c>MesesParcialesForzados: []</c> (lista vacía, no null): el consultor declara "ningún
    /// mes parcial", así que la heurística automática de <c>ConsumoCalculador</c> no se aplica. Sin
    /// esto, la caída de portafolio (deliberada, entre marzo y abril, por diseño del fixture) dispara
    /// la heurística sobre el mes de mayo y lo marcaría parcial — ruido ajeno a lo que este test
    /// verifica.</summary>
    private static ContextoInformeValor Contexto() => new(
        new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30), new DateOnly(2026, 6, 30),
        MesesParcialesForzados: []);

    /// <summary>
    /// El caso central: los cuatro mecanismos del balde 3 (dejó de facturar, vivo-cuesta-menos,
    /// vivo-cuesta-más, nuevo) y las dos fuentes de atribución (recomendación resuelta, balde 2;
    /// reserva confirmada, balde 1) presentes a la vez, más el contraste que exige E9: una reserva
    /// elegible (arrancó DENTRO de la ventana) contra una que no lo es (arrancó ANTES).
    ///
    /// <list type="bullet">
    /// <item>vm-dropped: base 100×3, cierre nada → delta +100 (dejó de facturar).</item>
    /// <item>vm-cheaper: base 200×3, cierre 80×3 → delta +120 (vivo, cuesta menos).</item>
    /// <item>vm-pricier: base 50×3, cierre 90×3 → delta −40 (vivo, cuesta más).</item>
    /// <item>vm-new: base nada, cierre 70×3 → delta −70 (nuevo).</item>
    /// <item>vm-fixed: base 300×3, cierre 120×3 → delta +180, con hallazgo resuelto en abril → balde 2.</item>
    /// <item>vm-reservado-elegible: base 400×3, cierre 150×3 → delta +250; su reserva arranca en
    /// abril (2026-04, DENTRO de la ventana de cierre) → E9 sí la deja explicar el período → balde 1,
    /// excluida de los baldes 2 y 3.</item>
    /// <item>vm-reservado-viejo: base 60×3, cierre 50×3 → delta +10 (una baja de $10, AJENA a la
    /// reserva); su reserva arrancó en octubre de 2025, ANTES de que empiece la ventana → E9 dice que
    /// no explica nada del período, así que NO se excluye: cae en vivo-cuesta-menos, junto con
    /// vm-cheaper.</item>
    /// </list>
    ///
    /// Balde 1 = 250 (solo la elegible). Balde 2 = 180. Balde 3 = 100 + (120+10) − 40 − 70 = 120.
    /// Total = 250 + 180 + 120 = 550. Verificado también por la vía independiente (sin pasar por
    /// ningún balde): 100+120−40−70+180+250+10 = 550.
    /// </summary>
    [Fact]
    public void Los_tres_baldes_mas_el_crecimiento_suman_la_variacion_total_con_los_cuatro_mecanismos_y_las_dos_fuentes_a_la_vez()
    {
        var facturacion = new List<FacturacionRow>();
        facturacion.AddRange(Filas("vm-dropped", [100m, 100m, 100m, null, null, null]));
        facturacion.AddRange(Filas("vm-cheaper", [200m, 200m, 200m, 80m, 80m, 80m]));
        facturacion.AddRange(Filas("vm-pricier", [50m, 50m, 50m, 90m, 90m, 90m]));
        facturacion.AddRange(Filas("vm-new", [null, null, null, 70m, 70m, 70m]));
        facturacion.AddRange(Filas("vm-fixed", [300m, 300m, 300m, 120m, 120m, 120m]));
        facturacion.AddRange(Filas("vm-reservado-elegible", [400m, 400m, 400m, 150m, 150m, 150m]));
        facturacion.AddRange(Filas("vm-reservado-viejo", [60m, 60m, 60m, 50m, 50m, 50m]));

        var hallazgos = new[] { Hallazgo("vm-fixed", new DateOnly(2026, 4, 15)) };

        // Elegible: ExpiresOn 2027-04-10 - 1 año = 2026-04-10 ("2026-04"), dentro de la ventana.
        var reservaElegible = Reserva("r-elegible", "vm-reservado-elegible", expiresOn: "2027-04-10");
        // No elegible: ExpiresOn 2026-10-05 - 1 año = 2025-10-05 ("2025-10"), antes de la ventana.
        var reservaVieja = Reserva("r-vieja", "vm-reservado-viejo", expiresOn: "2026-10-05");
        var foto = new FotoReservas(
            Medido: true, Motivo: "ok", Errores: [], AlertDays: 30,
            CapturadaEn: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            Reservas: [reservaElegible, reservaVieja]);

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, filasAntesDeFusionar: facturacion.Count, casos: [], Insumos(hallazgos),
            nombreCliente: "Cliente de prueba", Contexto(), fotoReservas: foto);

        Assert.Empty(modelo.Consumo!.MesesParciales); // MesesParcialesForzados:[] sin sorpresas del auto-detect
        var variacion = modelo.Consumo!.VariacionConsumo;
        Assert.NotNull(variacion);
        Assert.NotNull(variacion!.Atribucion);

        // Balde 1 (reservas, E9): solo la elegible aporta; la vieja aporta cero al período.
        Assert.Equal(250m, variacion.Reservas.AporteAlPeriodo);
        Assert.Equal("sub-1|rg-1|vm-reservado-elegible", Assert.Single(variacion.Reservas.RecursosQueExplicanElPeriodo));

        // Balde 2 (recomendación resuelta).
        Assert.Equal(180m, variacion.Atribucion!.PorRecomendacion.Total);

        // Balde 3 (sin atribuir), con vm-reservado-viejo cayendo en vivo-cuesta-menos junto a vm-cheaper.
        Assert.Equal(100m, variacion.Atribucion.SinAtribuir.DejoDeFacturar.Total);
        Assert.Equal(130m, variacion.Atribucion.SinAtribuir.VivoCuestaMenos.Total); // 120 + 10
        Assert.Contains(variacion.Atribucion.SinAtribuir.VivoCuestaMenos.Recursos, r => r.ResourceName == "vm-reservado-viejo");
        Assert.Equal(-40m, variacion.Atribucion.SinAtribuir.VivoCuestaMas.Total);
        Assert.Equal(-70m, variacion.Atribucion.SinAtribuir.Nuevo.Total);
        Assert.Equal(120m, variacion.Atribucion.SinAtribuir.Total); // 100+130-40-70

        // La reserva elegible quedó anotada como excluida (E3); la vieja NO, porque nunca se excluyó.
        var excluido = Assert.Single(variacion.Atribucion.ExcluidosPorReserva);
        Assert.Equal("vm-reservado-elegible", excluido.ResourceName);
        Assert.DoesNotContain(variacion.Atribucion.ExcluidosPorReserva, r => r.ResourceName == "vm-reservado-viejo");

        // LA INVARIANTE: balde 1 + balde 2 + balde 3, ya redondeados cada uno, dan la variación
        // total exacta, al centavo.
        Assert.Equal(550m, variacion.Reservas.AporteAlPeriodo
            + variacion.Atribucion.PorRecomendacion.Total + variacion.Atribucion.SinAtribuir.Total);
        Assert.Equal(550m, variacion.VariacionTotal);

        // Contra el cálculo independiente (siete deltas, sin pasar por ningún balde ni por E9):
        // 100+120-40-70+180+250+10 = 550.
        Assert.Equal(550m, variacion.VariacionTotal);
    }

    /// <summary>
    /// El contraste que demuestra por qué E9 es necesario y no solo una precaución teórica: si la
    /// eligibilidad no filtrara por fecha (si "confirmada" alcanzara para excluir, sin mirar cuándo
    /// arrancó la reserva), vm-reservado-viejo desaparecería de los baldes 2 y 3 SIN aportar nada al
    /// balde 1 tampoco (su reserva es vieja, aporta cero al período) — los $10 de esa baja se
    /// perderían de la suma total, y la invariante de arriba fallaría por exactamente esa cantidad
    /// (540 en vez de 550). Este test aísla esa comprobación: sin la reserva vieja en el fixture, el
    /// total tiene que bajar en exactamente esos $10.
    /// </summary>
    [Fact]
    public void Sin_la_reserva_vieja_del_fixture_la_variacion_total_baja_exactamente_en_su_delta()
    {
        var facturacion = new List<FacturacionRow>();
        facturacion.AddRange(Filas("vm-dropped", [100m, 100m, 100m, null, null, null]));
        facturacion.AddRange(Filas("vm-cheaper", [200m, 200m, 200m, 80m, 80m, 80m]));
        facturacion.AddRange(Filas("vm-pricier", [50m, 50m, 50m, 90m, 90m, 90m]));
        facturacion.AddRange(Filas("vm-new", [null, null, null, 70m, 70m, 70m]));
        facturacion.AddRange(Filas("vm-fixed", [300m, 300m, 300m, 120m, 120m, 120m]));
        facturacion.AddRange(Filas("vm-reservado-elegible", [400m, 400m, 400m, 150m, 150m, 150m]));
        // vm-reservado-viejo: OMITIDO a propósito (era el único que aportaba esos $10).

        var hallazgos = new[] { Hallazgo("vm-fixed", new DateOnly(2026, 4, 15)) };
        var reservaElegible = Reserva("r-elegible", "vm-reservado-elegible", expiresOn: "2027-04-10");
        var foto = new FotoReservas(
            Medido: true, Motivo: "ok", Errores: [], AlertDays: 30,
            CapturadaEn: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), Reservas: [reservaElegible]);

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, filasAntesDeFusionar: facturacion.Count, casos: [], Insumos(hallazgos),
            nombreCliente: "Cliente de prueba", Contexto(), fotoReservas: foto);

        Assert.Equal(540m, modelo.Consumo!.VariacionConsumo!.VariacionTotal); // 550 - 10
    }

    /// <summary>
    /// La carga en dos fases (<c>/preview</c> primero, <c>/preview/variacion-consumo</c> después, con
    /// la foto de reservas que la primera no paga) no puede cambiar ninguna cifra: lo que devuelve
    /// <see cref="InformeValorEnsamblador.EnsamblarVariacionConsumo"/> tiene que ser exactamente lo
    /// que <see cref="InformeValorEnsamblador.Ensamblar"/> habría puesto en <c>fact.variacionConsumo</c>
    /// si se le hubiera pasado esa misma foto de una sola vez. Sobre el fixture completo, con los tres
    /// baldes poblados, la invariante de E1 y la exclusión de E9 en juego.
    /// </summary>
    [Fact]
    public void La_fase_2_devuelve_el_mismo_bloque_que_habria_devuelto_el_ensamblado_de_una_sola_vez()
    {
        var facturacion = new List<FacturacionRow>();
        facturacion.AddRange(Filas("vm-dropped", [100m, 100m, 100m, null, null, null]));
        facturacion.AddRange(Filas("vm-cheaper", [200m, 200m, 200m, 80m, 80m, 80m]));
        facturacion.AddRange(Filas("vm-fixed", [300m, 300m, 300m, 120m, 120m, 120m]));
        facturacion.AddRange(Filas("vm-reservado-elegible", [400m, 400m, 400m, 150m, 150m, 150m]));

        var insumos = Insumos([Hallazgo("vm-fixed", new DateOnly(2026, 4, 15))]);
        var foto = new FotoReservas(
            Medido: true, Motivo: "ok", Errores: [], AlertDays: 30,
            CapturadaEn: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            Reservas: [Reserva("r-elegible", "vm-reservado-elegible", expiresOn: "2027-04-10")]);

        var deUnaSolaVez = InformeValorEnsamblador.Ensamblar(
            facturacion, filasAntesDeFusionar: facturacion.Count, casos: [], insumos,
            nombreCliente: "Cliente de prueba", Contexto(), fotoReservas: foto).Consumo!.VariacionConsumo;

        // La fase 2 recibe SOLO los hallazgos resueltos (lo único que el bloque lee de InsumosBd):
        // es el mismo insumo que la fase 1 sacó del record completo.
        var deLaFase2 = InformeValorEnsamblador.EnsamblarVariacionConsumo(
            facturacion, insumos.HallazgosResueltos!, Contexto(), foto);

        // Se comparan serializados y no con la igualdad de record: los baldes son listas, y la
        // igualdad sintetizada de un record compara sus miembros de referencia por referencia, así
        // que dos bloques idénticos campo por campo darían distinto. Serializar compara el contenido
        // completo -- cada recurso de cada balde, no solo los totales -- que es lo que este test
        // necesita afirmar.
        Assert.Equal(JsonSerializer.Serialize(deUnaSolaVez), JsonSerializer.Serialize(deLaFase2));
        Assert.Equal(250m, deLaFase2.Reservas.AporteAlPeriodo);
        Assert.NotNull(deLaFase2.VariacionTotal);
    }

    /// <summary>
    /// La fase 1 (<c>/preview</c>, sin foto) publica el eje "no medido" y su motivo dice que el dato
    /// se pide aparte. Es lo único que distingue esta respuesta de la de un cliente sin ninguna
    /// reserva: las dos publican el balde en cero, y un cero que puede significar dos cosas es
    /// exactamente lo que este módulo corrige en todos sus bloques.
    /// </summary>
    [Fact]
    public void Sin_foto_el_eje_sale_no_medido_y_el_motivo_dice_que_se_pide_aparte()
    {
        var facturacion = Filas("vm-cheaper", [200m, 200m, 200m, 80m, 80m, 80m]).ToList();

        var modelo = InformeValorEnsamblador.Ensamblar(
            facturacion, filasAntesDeFusionar: facturacion.Count, casos: [], Insumos([]),
            nombreCliente: "Cliente de prueba", Contexto());

        var reservas = modelo.Consumo!.VariacionConsumo!.Reservas;
        Assert.False(reservas.Medido);
        Assert.Contains("aparte", reservas.Motivo, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(reservas.Confirmados);
        Assert.Empty(reservas.RecursosQueExplicanElPeriodo);
        // El total sigue siendo el del portafolio completo: el balde de reservas mueve recursos entre
        // baldes, no cambia la variación total. La caída de vm-cheaper (200 -> 80) cae entera en el
        // balde sin atribuir.
        Assert.Equal(120m, modelo.Consumo!.VariacionConsumo!.VariacionTotal);
    }
}
