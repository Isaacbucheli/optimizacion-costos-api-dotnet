using OptimizacionCostos.Api.Features.Cdc;
using OptimizacionCostos.Api.Features.InformeValor.Recolector;

namespace OptimizacionCostos.Api.Tests.InformeValor;

/// <summary>
/// No tocan la base ni Azure: <see cref="FakeReservationService"/> y
/// <see cref="FakeAzureReservationsClient"/> son falsos en memoria (mismo patrón que
/// <c>FakeAccessReviewStore</c> de RbacRecolectorTests). Cubren la Tarea 1 del plan de la entrega
/// 2d (E2, E7): la foto se construye a partir de <c>IReservationService</c>/
/// <c>IAzureReservationsClient</c> (Cdc, ya desplegados), nunca del insumo de facturacion.
/// </summary>
public sealed class ReservasRecolectorTests
{
    private const int ClientId = 7;

    private static CredentialRef Cred(int id = 1, string? nombre = "cred-1") => new(id, nombre);

    private static ReservationDto Reserva(
        string? id = "r1", int credencial = 1, string? nombre = "Reserva 1", string? producto = "Standard_D2s_v5",
        string? region = "eastus", int? cantidad = 1, string? term = "P1Y", string? termLabel = "1 ano",
        string? estado = "Succeeded", string? expiresOn = "2027-01-01", int diasRestantes = 300,
        bool vencida = false, bool proximaAVencer = false, string? utilUltimo = "85%", string? util7d = "80%") =>
        new(id, credencial, nombre, producto, region, cantidad, term, termLabel, estado, null, [],
            expiresOn, diasRestantes, vencida, proximaAVencer, utilUltimo, util7d);

    private static ReservationConsumer Consumidor(
        string instanceId = "/subscriptions/s1/resourceGroups/rg1/providers/Microsoft.Compute/virtualMachines/vm1",
        string? nombreRecurso = "vm1", string? grupo = "rg1", string? suscripcion = "s1",
        string? sku = "Standard_D2s_v5", double horasUsadas = 700, string? ultimaVez = "2026-07-30", int diasVisto = 30) =>
        new(instanceId, nombreRecurso, grupo, suscripcion, null, sku, horasUsadas, ultimaVez, diasVisto);

    private sealed class FakeReservationService(
        IReadOnlyList<CredentialRef> credenciales,
        IReadOnlyList<ReservationDto> reservas,
        IReadOnlyList<object> errores) : IReservationService
    {
        public Task<IReadOnlyList<CredentialRef>> ActiveCredentialsAsync(int clientId, CancellationToken ct = default) =>
            Task.FromResult(credenciales);

        public Task<(IReadOnlyList<ReservationDto> Reservations, IReadOnlyList<object> Errors)> FetchAllAsync(
            IReadOnlyList<CredentialRef> creds, int alertDays, bool includeUtilization, CancellationToken ct = default) =>
            Task.FromResult((reservas, errores));

        public Task<IReadOnlyDictionary<string, object?>> ListClientReservationsAsync(
            IReadOnlyList<CredentialRef> creds, int alertDays, bool includeUtilization, CancellationToken ct = default) =>
            throw new NotSupportedException("El recolector usa FetchAllAsync directo, no este wrapper de dict.");
    }

    /// <summary>Consumidores por reservationId; una reserva ausente del diccionario o cuyo delegate
    /// lanza simula la falla puntual de <c>GetConsumersAsync</c> para esa reserva sola.</summary>
    private sealed class FakeAzureReservationsClient(
        Dictionary<string, Func<IReadOnlyList<ReservationConsumer>>> consumidoresPorReserva) : IAzureReservationsClient
    {
        public Task<IReadOnlyList<ReservationDto>> FetchForCredentialAsync(int credentialId, int alertDays, DateOnly today, bool includeUtilization, CancellationToken ct = default) =>
            throw new NotSupportedException("Lo resuelve IReservationService en este recolector.");

        public Task<(string Last, string Avg7d)> GetUtilizationAsync(int credentialId, string reservationId, CancellationToken ct = default) =>
            throw new NotSupportedException("La utilizacion ya viaja en ReservationDto (includeUtilization).");

        public Task<IReadOnlyList<ReservationConsumer>> GetConsumersAsync(int credentialId, string reservationId, int days, CancellationToken ct = default)
        {
            if (!consumidoresPorReserva.TryGetValue(reservationId, out var fn))
                return Task.FromResult((IReadOnlyList<ReservationConsumer>)[]);
            return Task.FromResult(fn());
        }
    }

    // ── E7: sin credenciales activas -> el eje no se midio (no es "cliente sin reservas") ──

    [Fact]
    public async Task Sin_credenciales_activas_el_eje_no_se_mide()
    {
        var svc = new FakeReservationService([], [], []);
        var client = new FakeAzureReservationsClient([]);

        var foto = await ReservasRecolector.CapturarAsync(svc, client, ClientId);

        Assert.False(foto.Medido);
        Assert.Empty(foto.Reservas);
        Assert.NotEmpty(foto.Motivo);
    }

    // ── E2: si la lectura de reservas trae errores, el eje no se mide (no sale en cero) ──

    [Fact]
    public async Task Con_errores_de_lectura_el_eje_no_se_mide_y_no_sale_en_cero()
    {
        var errores = new List<object> { new { credential_id = 1, error = "Forbidden" } };
        var svc = new FakeReservationService([Cred()], [Reserva()], errores);
        var client = new FakeAzureReservationsClient([]);

        var foto = await ReservasRecolector.CapturarAsync(svc, client, ClientId);

        Assert.False(foto.Medido);
        Assert.Empty(foto.Reservas); // no se publica un balde parcial como si fuera el total
        Assert.Same(errores, foto.Errores);
    }

    [Fact]
    public async Task Con_credenciales_y_sin_errores_pero_sin_reservas_es_un_cero_legitimo()
    {
        var svc = new FakeReservationService([Cred()], [], []);
        var client = new FakeAzureReservationsClient([]);

        var foto = await ReservasRecolector.CapturarAsync(svc, client, ClientId);

        Assert.True(foto.Medido);
        Assert.Empty(foto.Reservas);
    }

    // ── E7: las vencidas no entran; las proximas a vencer si, marcadas ──

    [Fact]
    public async Task Una_reserva_vencida_no_entra_a_la_foto()
    {
        var vencida = Reserva(id: "r-vencida", vencida: true);
        var activa = Reserva(id: "r-activa", vencida: false);
        var svc = new FakeReservationService([Cred()], [vencida, activa], []);
        var client = new FakeAzureReservationsClient([]);

        var foto = await ReservasRecolector.CapturarAsync(svc, client, ClientId);

        var fila = Assert.Single(foto.Reservas);
        Assert.Equal("r-activa", fila.ReservationId);
    }

    [Fact]
    public async Task Una_reserva_proxima_a_vencer_entra_y_queda_marcada()
    {
        var proxima = Reserva(id: "r-proxima", vencida: false, proximaAVencer: true);
        var svc = new FakeReservationService([Cred()], [proxima], []);
        var client = new FakeAzureReservationsClient([]);

        var foto = await ReservasRecolector.CapturarAsync(svc, client, ClientId);

        var fila = Assert.Single(foto.Reservas);
        Assert.True(fila.Expiring);
    }

    // ── E7: el umbral de "proxima a vencer" viaja dentro de la foto ──

    [Fact]
    public async Task El_umbral_de_dias_viaja_dentro_de_la_foto()
    {
        var svc = new FakeReservationService([Cred()], [], []);
        var client = new FakeAzureReservationsClient([]);

        var foto = await ReservasRecolector.CapturarAsync(svc, client, ClientId, alertDays: 45);

        Assert.Equal(45, foto.AlertDays);
    }

    [Fact]
    public void El_umbral_por_defecto_coincide_con_el_de_la_pantalla_de_reservas()
    {
        // CdcController usa alert_days = 30 por defecto (Features/Cdc/Api/CdcController.cs).
        Assert.Equal(30, ReservasRecolector.AlertDaysPorDefecto);
    }

    // ── E2: consumidores confirmados por terna, y el resto de la cantidad queda como estimado ──

    [Fact]
    public async Task Los_consumidores_confirmados_viajan_por_terna_con_sus_horas()
    {
        var reserva = Reserva(id: "r1", cantidad: 3);
        var consumidor = Consumidor(nombreRecurso: "vm1", grupo: "rg1", suscripcion: "s1", horasUsadas: 700);
        var svc = new FakeReservationService([Cred()], [reserva], []);
        var client = new FakeAzureReservationsClient(new()
        {
            ["r1"] = () => [consumidor],
        });

        var foto = await ReservasRecolector.CapturarAsync(svc, client, ClientId);

        var fila = Assert.Single(foto.Reservas);
        var c = Assert.Single(fila.Consumidores);
        Assert.Equal("vm1", c.ResourceName);
        Assert.Equal("rg1", c.ResourceGroup);
        Assert.Equal("s1", c.SubscriptionId);
        Assert.Equal(700, c.UsedHours);
    }

    [Fact]
    public async Task La_cantidad_reservada_sin_consumidor_confirmado_queda_como_estimada()
    {
        var reserva = Reserva(id: "r1", cantidad: 3);
        var unSoloConsumidor = Consumidor();
        var svc = new FakeReservationService([Cred()], [reserva], []);
        var client = new FakeAzureReservationsClient(new()
        {
            ["r1"] = () => [unSoloConsumidor],
        });

        var foto = await ReservasRecolector.CapturarAsync(svc, client, ClientId);

        var fila = Assert.Single(foto.Reservas);
        Assert.Single(fila.Consumidores);
        Assert.Equal(2, fila.UnidadesEstimadas); // 3 reservadas - 1 confirmado
    }

    /// <summary>Mismo criterio que RiCoverageService.MatchConfirmed: un consumidor sin horas
    /// usadas no es una confirmacion real, es ruido del reporte de Consumption.</summary>
    [Fact]
    public async Task Un_consumidor_con_cero_horas_no_cuenta_como_confirmado()
    {
        var reserva = Reserva(id: "r1", cantidad: 1);
        var consumidorVacio = Consumidor(horasUsadas: 0);
        var svc = new FakeReservationService([Cred()], [reserva], []);
        var client = new FakeAzureReservationsClient(new()
        {
            ["r1"] = () => [consumidorVacio],
        });

        var foto = await ReservasRecolector.CapturarAsync(svc, client, ClientId);

        var fila = Assert.Single(foto.Reservas);
        Assert.Empty(fila.Consumidores);
        Assert.Equal(1, fila.UnidadesEstimadas);
    }

    /// <summary>Una falla puntual de Consumption para UNA reserva no tumba toda la foto (mismo
    /// criterio que el catch silencioso de RiCoverageService.ComputeAsync alrededor de
    /// GetConsumersAsync): esa reserva sola queda sin confirmados, con su cantidad completa como
    /// estimada.</summary>
    [Fact]
    public async Task Una_falla_puntual_de_consumidores_no_tumba_toda_la_foto()
    {
        var reserva = Reserva(id: "r1", cantidad: 2);
        var svc = new FakeReservationService([Cred()], [reserva], []);
        var client = new FakeAzureReservationsClient(new()
        {
            ["r1"] = () => throw new InvalidOperationException("403 Consumption"),
        });

        var foto = await ReservasRecolector.CapturarAsync(svc, client, ClientId);

        Assert.True(foto.Medido);
        var fila = Assert.Single(foto.Reservas);
        Assert.Empty(fila.Consumidores);
        Assert.Equal(2, fila.UnidadesEstimadas);
    }

    [Fact]
    public async Task La_foto_registra_el_instante_de_captura()
    {
        var svc = new FakeReservationService([Cred()], [], []);
        var client = new FakeAzureReservationsClient([]);

        var antes = DateTime.UtcNow;
        var foto = await ReservasRecolector.CapturarAsync(svc, client, ClientId);
        var despues = DateTime.UtcNow;

        Assert.InRange(foto.CapturadaEn, antes.AddSeconds(-1), despues.AddSeconds(1));
    }
}
