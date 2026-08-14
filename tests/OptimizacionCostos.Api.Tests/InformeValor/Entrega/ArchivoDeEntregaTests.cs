using OptimizacionCostos.Api.Features.InformeValor.Entrega;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor.Entrega;

/// <summary>
/// F4: en el archivo va todo lo que haga falta para que el mismo informe, reemitido, dé el mismo
/// resultado. Estos tests cubren las dos piezas del archivo donde un descuido de serialización
/// cambia el resultado sin fallar: la foto de reservas y el tri-estado de los meses parciales.
/// </summary>
public sealed class ArchivoDeEntregaTests
{
    // ================================================================================
    // La foto de reservas
    // ================================================================================

    private static FotoReservas FotoDeEjemplo() => new(
        Medido: true,
        Motivo: "Las reservas activas se leyeron completas desde Azure.",
        Errores: [],
        AlertDays: 30,
        CapturadaEn: new DateTime(2026, 3, 1, 12, 30, 0, DateTimeKind.Utc),
        Reservas:
        [
            new ReservaActiva(
                ReservationId: "res-1", Nombre: "Reserva de cómputo", Producto: "Standard_D4s_v5",
                Region: "eastus", Cantidad: 4, Term: "P1Y", TermLabel: "1 año",
                ExpiresOn: "2026-11-30", DaysRemaining: 274, Expiring: false,
                UtilizationLast: "98", Utilization7d: "97",
                Consumidores:
                [
                    new ConsumidorReserva("/subscriptions/s/rg/r", "vm-1", "rg-1", "sub-1", "Standard_D4s_v5", 720d, "2026-02-28", 28),
                ],
                UnidadesEstimadas: 3, ConsumidoresNoLeidos: false),
        ]);

    /// <summary>
    /// La foto tiene que volver de la base igual a como entró. Sin esto, reemitir un informe viejo
    /// lo recalcularía contra las reservas de HOY, que es justo lo que archivarla evita.
    /// </summary>
    [Fact]
    public void La_foto_de_reservas_sobrevive_la_ida_y_la_vuelta()
    {
        var original = FotoDeEjemplo();

        var vuelta = FotoReservasJson.Deserializar(FotoReservasJson.Serializar(original))!;

        Assert.True(vuelta.Medido);
        Assert.Equal(original.Motivo, vuelta.Motivo);
        Assert.Equal(30, vuelta.AlertDays);
        Assert.Equal(original.CapturadaEn, vuelta.CapturadaEn);
        var r = Assert.Single(vuelta.Reservas);
        Assert.Equal("res-1", r.ReservationId);
        Assert.Equal("Reserva de cómputo", r.Nombre);
        Assert.Equal(4, r.Cantidad);
        Assert.Equal("2026-11-30", r.ExpiresOn);
        Assert.Equal(3, r.UnidadesEstimadas);
        Assert.False(r.ConsumidoresNoLeidos);
        var c = Assert.Single(r.Consumidores);
        Assert.Equal("vm-1", c.ResourceName);
        Assert.Equal("rg-1", c.ResourceGroup);
        Assert.Equal("sub-1", c.SubscriptionId);
        Assert.Equal(720d, c.UsedHours);
    }

    /// <summary>
    /// El eje no medido tiene que sobrevivir con su motivo. Una foto que vuelve con
    /// <c>Medido=false</c> y sin motivo es indistinguible de "el cliente no tiene reservas", que es
    /// una afirmación distinta y falsa.
    /// </summary>
    [Fact]
    public void Una_foto_no_medida_vuelve_con_su_motivo_y_sus_errores()
    {
        var original = new FotoReservas(
            Medido: false,
            Motivo: "La lectura de reservas falló para al menos una credencial.",
            Errores: [new { error = "RequestFailedException" }],
            AlertDays: 30,
            CapturadaEn: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Reservas: []);

        var vuelta = FotoReservasJson.Deserializar(FotoReservasJson.Serializar(original))!;

        Assert.False(vuelta.Medido);
        Assert.Equal(original.Motivo, vuelta.Motivo);
        Assert.Single(vuelta.Errores);
        Assert.Empty(vuelta.Reservas);
    }

    /// <summary>
    /// Columna vacía y foto con <c>Medido=false</c> son dos hechos distintos: "esta entrega se
    /// generó sin capturar reservas" contra "se intentó y no se pudo". Colapsarlos deja al
    /// consultor sin forma de saber cuál de los dos pasó.
    /// </summary>
    [Fact]
    public void Sin_foto_la_columna_queda_nula_y_no_una_foto_no_medida()
    {
        Assert.Null(FotoReservasJson.Serializar(null));
        Assert.Null(FotoReservasJson.Deserializar(null));
        Assert.Null(FotoReservasJson.Deserializar("   "));
    }

    /// <summary>Un JSON corrupto no se traduce a "no había foto": se propaga. Los dos casos llevan
    /// a decisiones opuestas.</summary>
    [Fact]
    public void Una_foto_corrupta_no_se_lee_como_ausente() =>
        Assert.ThrowsAny<Exception>(() => FotoReservasJson.Deserializar("{\"Medido\":"));

    // ================================================================================
    // El tri-estado de los meses parciales
    // ================================================================================

    /// <summary>
    /// Los tres estados del spec §12.3.3 reemiten distinto: sin declaración manda la heurística
    /// automática, la lista vacía la desactiva, y la lista con elementos fija exactamente esos
    /// meses. Guardar la lista vacía como NULL —el error fácil— resucita la heurística que el
    /// consultor había apagado a propósito.
    /// </summary>
    [Fact]
    public void Los_tres_estados_de_meses_parciales_se_guardan_distinto()
    {
        Assert.Null(MesesParcialesJson.Serializar(null));
        Assert.Equal("[]", MesesParcialesJson.Serializar([]));
        Assert.Equal("[\"2026-01\"]", MesesParcialesJson.Serializar(["2026-01"]));
    }

    [Fact]
    public void Los_tres_estados_de_meses_parciales_vuelven_distinto()
    {
        Assert.Null(MesesParcialesJson.Deserializar(null));
        Assert.Empty(MesesParcialesJson.Deserializar("[]")!);
        Assert.Equal(["2026-01", "2026-02"], MesesParcialesJson.Deserializar("[\"2026-01\",\"2026-02\"]")!);
    }

    // ================================================================================
    // Las claves que viajan a la base y al artefacto
    // ================================================================================

    /// <summary>Una sola grafía por concepto: la que se guarda en <c>bloques_publicados</c> es la
    /// misma que lee la capa de dibujo y la misma que acepta la API.</summary>
    [Fact]
    public void Cada_bloque_economico_va_y_vuelve_por_su_clave()
    {
        foreach (var b in BloqueEconomicoExtensions.Todos)
            Assert.Equal(b, BloqueEconomicoExtensions.Parsear(b.Clave()));

        Assert.Equal(8, BloqueEconomicoExtensions.Todos.Select(b => b.Clave()).Distinct().Count());
        Assert.Null(BloqueEconomicoExtensions.Parsear("gasto_total"));
        Assert.Null(BloqueEconomicoExtensions.Parsear(null));
    }

    [Fact]
    public void Cada_variante_va_y_vuelve_por_su_clave()
    {
        Assert.Equal(VarianteInforme.Interna, VarianteInformeExtensions.Parsear("interna"));
        Assert.Equal(VarianteInforme.Cliente, VarianteInformeExtensions.Parsear("Cliente"));
        Assert.Null(VarianteInformeExtensions.Parsear("completa"));
    }
}
